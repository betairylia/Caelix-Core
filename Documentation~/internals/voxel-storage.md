# The VDB-like voxel storage model

Status: Current implementation. Checked against Core `e5ead50` on 2026-09-06.

Caelix stores large voxel entities as a sparse map of sectors. A sector maps brick
coordinates to compact storage IDs, and each allocated brick stores a small dense
volume. This is the useful meaning of “VDB-like” here: a sparse hierarchy with
dense leaves. The implementation uses a fixed sector/brick layout; it does not
implement OpenVDB's tree, tiles, or file format.

## Follow a voxel through the hierarchy

![An entity maps sector coordinates to handles; the sector maps absolute brick indices to shared compact IDs, which address dense slot data and optional occupancy bitmaps.](../images/voxel-storage.svg)

*Figure 1. Coordinate-table indices and compact storage IDs serve different
purposes. An allocated brick can contain only empty Blocks.*

```text
VoxelEntityData
  sectors: entity-local sector coordinate -> SectorHandle
    Sector: 16 x 16 x 16 brick coordinates
      brickMap: absolute brick index -> compact brick ID, or EMPTY
        slot storage: compact brick ID -> 8 x 8 x 8 values
        optional aux: compact brick ID -> derived summary, such as a bitmap
```

| Level | Meaning |
|---|---|
| Entity | Has identity, rigid transform, and sparse sector maps. |
| Sector | Covers 128 blocks per axis and has 4,096 possible brick positions. |
| Brick | Covers 8 blocks per axis: 512 values per created slot. |
| Slot | A typed per-voxel data plane sharing the sector's brick IDs. |
| Aux buffer | Derived per-brick data associated with a slot, such as occupancy bits. |

The Block slot contains the visible voxel value. In this revision `Block.data`
is a `ushort` (16 bits). Metadata lives in a separate slot. Empty Block means
`data == 0`. Keep variable-length gameplay data outside voxel records.

## Application-wide slot definitions

Maintainer contract, agreed 2026-09-10: each slot id has one application-wide
meaning, binary layout and stride (bytes per voxel). Define these during initial
application setup. Mods may contribute definitions during that setup; all
definitions must be fixed before worlds are created or loaded and clients connect.
Once defined, a slot cannot change its meaning, layout or stride during the run.

The same definition applies to every world, entity, sector and brick, on both
server and client. Freeing or recreating storage does not reset it. Changing a
slot's stride violates the contract and has undefined behavior; callers must not
depend on a replacement or receive-flush boundary accepting a different stride.
Data from another slot schema requires explicit conversion before use.

This is an application contract at this stage. Core does not yet provide a
frozen app-level slot registry or comprehensive validation against it. Existing
storage and replication stride checks remain useful diagnostics, but do not
establish or redefine a slot's application-wide meaning.

## Coordinates and IDs

### Experimental packed scene materials

The `dev/astra/raw-color-materials` experiment selects its encoding at compile time.
Keep `CAELIX_PACKED_SCENE_COLOR` in `Runtime/BlockEncoding.cs` and Caelix's
`Runtime/Rendering/Shaders/def/CaelixMaterialConfig.hlsl` enabled together. Comment
out both defines to use regular block IDs. Restart with the matching scene data;
there is no runtime switch or additional encoding marker in `.cxw` files.

| Prefix | Remaining bits |
|---|---|
| `1` | RGB555, including opaque black. |
| `01` | RGB444 and two emission-strength bits (1, 8, 64, 512). |
| `00` | Six derived face bits and an eight-bit shared glass ID; ID zero is air. |

Canonical Block data stores glass IDs without face bits. The renderer adds faces
when preparing its data. The shared glass palette is generated from Caelix's
`Tools~/RawColor/glass-palette.json` for both C# and HLSL. Emission also has a global
shader multiplier, `CAELIX_PACKED_EMISSION_SCALE`. Packed mode disables world
automata because scene color values do not identify gameplay blocks.

Caelix's `Tools~/RawColor` exports native v6 `.cxw` saves directly from voxelized
source materials. Existing RGB-only VL32 files cannot recover lost emission or
glass properties. Keep generated scenes outside Unity package/project folders.

Entity-local voxel coordinates can be negative. Sector coordinates select the
sector; sector-local voxel coordinates range from 0 to 127. Brick coordinates
inside a sector range from 0 to 15. Voxel coordinates inside a brick range from
0 to 7. A voxel `(x, y, z)` occupies the unit box from that coordinate to the next
integer on each axis.

An **absolute brick index** addresses a position in the 4,096-entry sector table.
A **compact brick ID** addresses allocated slot data. They are different index
spaces. `SectorNonEmptyBrickEnumerator.BrickRef` exposes both as `BrickAbs` and
`Bid`. Use the conversion/access methods on `Sector`; use the
[enumerators](../reference/enumerators.md) for traversal.

For example, entity-local voxel `(-1, 0, 0)` lies in sector `(-1, 0, 0)` at
sector-local voxel `(127, 0, 0)`. Ordinary integer division that truncates toward
zero gives the wrong sector for this case. The existing entity accessors handle
the coordinate split.

Entities use translation and rotation with scale 1. A `VoxelEntityData` is a
Caelix storage object; it is not a Unity ECS `Entity`.

## Sparse allocation

Missing sectors and bricks read as empty through the appropriate entity/sector
accessors. Writing a default value to a missing brick does not allocate that
brick. A non-default write can allocate a brick and its required slot storage.
Every created slot uses the same compact brick ID space.

Clearing every Block in a brick does not automatically free the brick. Allocation
counts and `EnumerateNonEmptyBricks()` therefore describe allocated storage.
Other slots may also have data where Block is empty. Occupied Block traversal
uses a refreshed bitmap and has a different meaning from allocation traversal.

The sector still has a fixed coordinate table and flag arrays. Sparse leaf data
does not mean every operation scales only with occupied voxels: the current
allocated-brick enumerator scans the coordinate table internally.

## Ownership and snapshots

`SectorHandle` holds a pointer to a sector. Copies refer to the same allocation.
`VoxelEntityData` contains `SharedHashMap` values whose copies share the map
header. Copying these structs does not create independent storage. Copying an
entity also copies scalar fields, so orchestration code must write changed scalar
state back through the owning world API.

The owner must complete jobs before disposing or replacing storage. Never dispose
a borrowed handle or map. Do not retain brick pointers or enumerators across
allocation, snapshot application, entity replacement, or sector removal.

`VoxelEntityData.RemoveSectorAt` first marks the facing bricks of every surviving
neighbour sector dirty with `GeometryWithLocalNeighbor` and an empty direction
mask, so consumers that culled boundary data against the removed storage are
rescheduled by the next propagation and no sector is created.

`BrickBatchApplier` (`Runtime/Net/`) accepts unmanaged message payloads keyed by
brick and is the only apply path for replication. Its caller must keep each payload
alive until `Flush` returns. The applier validates every message before it changes
any brick, groups the records by the storage unit that owns them, applies different
units in parallel while keeping the wire order inside one unit, publishes a
`Removed` change entry per freed brick, frees a unit that ended up empty, and
refreshes `NonEmptyBricks` wherever allocation changed. It preserves slot strides,
render dirty flags and grow-only brick bounds. A malformed message changes no brick,
creates no storage, and reports an error code for the main thread.

During an active sector snapshot, normal reads use the live data and writes go
to the snapshot buffers. After scheduled work completes, applying the snapshot
commits the writes. This supports automata reading a stable state for the stage.
It does not make two jobs writing the same voxel safe.

Snapshot activation copies into the existing pending slot buffers. Data and aux
allocations retain capacity across swaps and grow only when needed; slots or aux
buffers absent from the source are released. This removes repeated allocation for
stable storage without removing the copy needed to initialize the next snapshot.
Each committed swap still invalidates previously bound storage access.

Occupancy aux data must be refreshed after committed edits and local dirty
propagation. `VoxelEntityData.RefreshNonEmptyMask()` uses Geometry require-update
flags when an aux buffer already exists. Merely calling it after a raw write,
without the propagation phase, does not guarantee a changed mask is rebuilt.

## Regions and automata access

A `VoxelRegion` is the residency and generation unit: a fixed-size block box of one
entity, in entity-local coordinates, sized `VoxelRegion.SizeInBlocks` per axis.
Never hardcode that size. A generator calls `VoxelRegion.Create`, fills the
detached region from jobs, and hands it to `VoxelEntityData.AttachRegion`, which
takes ownership and announces every brick it brought. `TryOpenRegion` returns an
attached view for in-place writes instead. `RemoveRegion` frees one.
`EnsureRegion`, `HasRegion`, `RegionCount`, `GetRegionPositions` and
`TryGetStorageBounds` describe residency, not content: a region contributes its
whole box to the bounds even when one brick in it is allocated.

`AutomataBrick` is one brick of automata work, opened by
`VoxelEntityData.OpenAutomataBrick(key)`. Its phase rule: reads see the FRONT
buffer, which holds the tick's start state, and writes land in the pending buffer.
Reads span the 27-brick window `Origin - 8 .. Origin + 15` per axis; writes are
restricted to the center brick, which is what keeps bricks independent. The stage
brackets its work with `BeginAutomataWrites` (opens the pending buffer of every
region that has require-update flags) and `EndAutomataWrites` (commits and
advances the storage epoch). Opening a brick never allocates; writing into one may.

The sector is the backend of both. A region wraps one sector, an automata brick
wraps a sector handle plus its neighbour handles, and no part of either API names
a sector or a sector-local coordinate.

## Implementation and validation

- [Sector constants and ownership](../../Runtime/Sector.cs)
- [Brick coordinate map](../../Runtime/SparseBrickIdTable.cs)
- [Slot storage and snapshot write selection](../../Runtime/Sector.Slots.cs)
- [Entity storage](../../Runtime/VoxelEntityData.cs), [shared maps](../../Runtime/SharedHashMap.cs)
- [Regions](../../Runtime/VoxelRegion.cs), [region API](../../Runtime/VoxelEntityData.Regions.cs)
- [Automata brick](../../Runtime/Automata/AutomataBrick.cs), [automata phase](../../Runtime/VoxelEntityData.Automata.cs)
- [Occupancy refresh](../../Runtime/VoxelEntityData.OccupancyMask.cs)
- [Storage tests](../../Tests/Editor/SectorStorageTests.cs), [snapshot tests](../../Tests/Editor/SectorSnapshotTests.cs), [coordinate tests](../../Tests/Editor/CoordinateAndNeighborhoodTests.cs)

This page was checked against source. The documentation pass did not rerun Unity tests.

## Voxel setters and simulation flags

`Sector.SetVoxelSlot` has two bodies chosen by overload, sharing the storage work
(`TryPrepareWrite`: resolve, allocate, read the previous value, early-out when
unchanged; `CommitWrite`: mark dirty, update the AABB, store):

- `SetBlock(x, y, z, block)` / `SetVoxelSlot(slot, x, y, z, value)`: simulation
  bits come from `AutomataDefaults` — `Blocks[previous.id] | Blocks[value.id]`
  for the Block slot, `Slots[slot]` otherwise.
- `SetBlock(..., simulationFlags)` / `SetVoxelSlot(..., simulationFlags)`: the
  caller's mask, masked to `BrickUpdateFlags.AutomataMask`. The tables are not
  read on this path.

Both bodies OR the engine's geometry bits (`Geometry | GeometryWithLocalNeighbor`)
onto every changed Block write and none onto other slots; `BlockBrickAdded` still
comes from allocation. A caller mask cannot suppress those bits, and under
collections checks a mask with engine bits throws. The same pair of overloads is
forwarded by `SectorHandle`, `VoxelRegion`, `VoxelEntityData` and `AutomataBrick`.
`Sector.MarkBrickSimulationDirty` (reached through `AutomataBrick.SetDirty`) raises
simulation bits on a brick with whole-brick propagation and no data change.

`RequiredBrick.IsAllocated` records whether a collected position held storage, so
a consumer can drop unallocated positions without opening the brick.
`VoxelEntityData.BeginAutomataWrites(mask)` activates snapshots only in sectors
whose require-update flags intersect the mask.

# Choose a voxel traversal API

Use the existing traversal APIs to discover bricks. Keep sector storage details
inside Core; gameplay, rendering, and physics use entity-local brick keys.

Source reviewed: Core `881efac3`, Caelix `3d013d8`, 2026-09-16.
Validation: source review only.

## Select an API

| Task | API on `VoxelEntityData` | Result / requirement |
|---|---|---|
| Visit allocated bricks | `EnumerateBricks()` | Entity-local `int3` brick keys; an allocated brick can contain only empty Blocks. |
| Visit an inclusive key range | `EnumerateBricks(minKey, maxKey)` | Allocated keys inside the range. Order is not a spatial sorting guarantee. |
| Collect scheduled work | `CollectRequiredBricks(entityId, mask, includeEmpty, into)` | Appends `RequiredBrick` records. Use a created `TempJob` or `Persistent` list, not `Temp`. |
| Read one brick's scheduled flags | `GetRequiredFlags(key)` | Required-update flags; no allocation. |
| Read source changes for replication/alien propagation | `EnumerateBrickDirtyFlags(mask, dirtyOnly)` | Allocated keys and masked dirty flags, before the end-of-tick clear. |
| Visit bitmap-selected slot values | `EnumerateBrickBitmask<T>(slot, key)` | Values and entity-local block positions; requires current slot bitmap data. |
| Bind a brick's data | `TryBindBrick<T>(slot, key, out data)` | Borrowed pointer; false when the brick or slot is absent. |

`BrickKey.FromBlock(position)` handles negative block positions.
`BrickKey.ToBlockOrigin(key)` returns the brick's first block. A brick has
`BrickKey.BlocksPerAxis` blocks per axis.

Dense loops inside an already selected brick are appropriate for cellular rules,
generation, and mask construction. Do not duplicate sector coordinate sweeps to
find allocated or scheduled bricks. Add a shared traversal API when one is missing.

## Scheduled work

The world collects automata-channel work once and gives every hook the same list.
Each `RequiredBrick` contains `Entity`, `Key`, `Flags`, and `IsAllocated`; filter
`Flags` for your channel. `includeEmpty: true` includes unallocated positions that
propagation selected, so growth rules can create cells there.

The collector appends to the destination; clear it before a fresh collection.
It derives flags and allocation from the absolute coordinate index. Consumers
can rely on those record fields and should not reconstruct sector indices.

### Current required-update limitation

The internal `SectorRequiredUpdateBrickEnumerator` still leaves its compact ID
unset while walking empty positions. The public `CollectRequiredBricks` path
resolves the coordinate index itself, so this does not invalidate `RequiredBrick`.
The old `CollectBrickJob` and its cached `BrickInfo` guidance no longer apply.

## Lifetime rules

- Complete users before allocation, snapshot commit, replacement, or removal.
  Rebind pointers and reset cursors after storage changes; see `StorageEpoch`.
- Enumerators, pointers, and opened neighborhoods borrow storage. Do not dispose
  that storage or retain a view between phases.
- Required flags describe scheduled work. Dirty flags describe source changes
  and are cleared at the end of the tick.
- Bitmap traversal sees committed data. Refresh occupancy after propagation;
  it does not see pending automata writes.

## Core internals and physics overlaps

Core's sector enumerators implement allocation, required/dirty filtering, and
bitmap traversal. `SectorNonEmptyBrickEnumerator` scans the 4,096-entry map;
using the shared API centralizes that scan rather than eliminating its cost.
Absolute coordinate indices and compact storage IDs are different spaces.

Physics provides `graph.TryGetOverlaps(source, out var neighbors)`. Its
`BrickOverlapKey` results identify an entity and an entity-local brick coordinate.
An overlap is conservative. Re-fetch the graph after each step, check its version,
and never dispose its borrowed buffers. See
[the overlap graph](https://github.com/betairylia/Caelix-physics/blob/main/Caelix.Physics/Runtime/Physics/BrickOverlapGraph.cs).

## Source and checks

- [Public brick APIs and collector](../../Runtime/VoxelEntityData.Bricks.cs)
- [Brick-key traversal](../../Runtime/BrickKeyEnumerator.cs)
- [Internal required/dirty traversal](../../Runtime/SectorHelpers.cs)
- [Storage lifetime](../internals/voxel-storage.md)
- [Facade tests](../../Tests/Editor/BrickFacadeTests.cs)
- [Cursor tests](../../Tests/Editor/BrickFacadeCursorTests.cs)
- [Automata tests](../../Tests/Editor/AutomataBrickTests.cs)

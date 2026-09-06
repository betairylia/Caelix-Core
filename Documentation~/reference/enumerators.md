# Use enumerators to traverse voxel data

Status: Maintainer rule and current API inventory. Checked against Core `e5ead50`
and Caelix `253d632` on 2026-09-06. Physics inventory checked at `c10c4e2`.

**Use the existing enumerators for voxel-storage traversal. Do not write a raw
loop over the 4,096 brick coordinates to find allocated, dirty, or required-update
bricks.** Choose the enumerator whose selection matches the task. Keep traversal
semantics and future optimizations in the shared implementation.

This rule applies to engine consumers, tools, examples, and new code. A loop inside
the enumerator implementation is expected. Dense per-brick algorithms, bitmap
construction, explicit generation of new geometry, and exhaustive test oracles may
need bounded loops; explain that purpose rather than using them as a replacement
for an available traversal API. If a required traversal is missing, add a shared
enumerator and focused tests instead of duplicating a coordinate sweep.

## Select an enumerator

All five dedicated voxel enumerator types are in Core's `Runtime` sources.

| Type | Entry point | Selects and returns | Preconditions |
|---|---|---|---|
| `SectorNonEmptyBrickEnumerator` | `sector.EnumerateNonEmptyBricks()` | Allocated bricks; `BrickRef.BrickAbs` is the coordinate-table index, `Bid` is the compact storage ID. | Stable sector allocation. Does not require dirty flags or occupancy masks. |
| `SectorRequiredUpdateBrickEnumerator` | `new SectorRequiredUpdateBrickEnumerator(sector, mask, includeEmpty)` | Positions selected by `brickRequireUpdateFlags`; `BrickIterator.position` is the sector-local block origin. | Use the required-update phase and the appropriate mask. See the `includeEmpty` limitation below. |
| `SectorDirtyBrickEnumerator` | `sector.EnumerateDirtyBricks(mask)` | Allocated bricks whose own raw dirty flags match; `DirtyBrickInfo.BrickIdx` is an absolute index and `Flags` contains the matched bits. | Before `EndTick` clears dirty flags; server replication uses it after simulation. |
| `SectorBitmaskSlotEnumerator<T>` | `sector.EnumerateBitmaskSlot<T>(slotId)` | Values selected by a slot's per-brick bitmap; `value` and sector-local voxel `position`. | Matching slot type/stride and refreshed `BrickBitmask` aux. No fallback data scan. |
| `BrickBitmaskSlotEnumerator<T>` | `sector.EnumerateBitmaskSlotInBrick<T>(slotId, bid, blockOrigin)` | Bitmap-selected values in one brick; positions include the supplied origin. | A valid compact brick ID and current bitmap. The empty-ID sentinel yields nothing. |

Convenience methods use the same bitmap enumerators:

| Method | Meaning |
|---|---|
| `EnumerateNonEmptyBlocks()` | `SectorBitmaskSlotEnumerator<Block>` over the Block occupancy mask. |
| `EnumeratePhysicsKeyBlocks()` | `SectorBitmaskSlotEnumerator<PhysicsInfo>` over the physics key mask. |
| `EnumeratePhysicsKeyBlocksInBrick(bid, blockOrigin)` | `BrickBitmaskSlotEnumerator<PhysicsInfo>` for one brick. |

“Non-empty brick” means allocated, even if all its Blocks are now empty. The base
enumerator currently scans the 4,096-entry map internally; using it centralizes
the scan, but does not make the current implementation an occupied-only index walk.

## Example: visit allocated bricks

This method receives a live, borrowed handle. Call it while no job or other code
is changing or disposing the sector. It returns the number of allocated bricks.

```csharp
using Caelix;

public static class BrickTraversalExample
{
    public static int CountAllocatedBricks(SectorHandle handle)
    {
        int count = 0;
        foreach (var brick in handle.Get().EnumerateNonEmptyBricks())
        {
            // brick.BrickAbs indexes flags; brick.Bid indexes slot data.
            count++;
        }
        return count;
    }
}
```

Use `NonEmptyBrickCount` when only the count is needed. This example demonstrates
the traversal pattern for work that actually needs to visit each brick.

## Lifetime and phase rules

- Read `Current` only after `MoveNext()` succeeds, or through `foreach`.
- Do not cache an enumerator or its pointers between ticks or snapshot swaps.
- Use required-update flags for scheduled consumer work. Raw dirty flags describe
  the source edits and are cleared at the end of the tick.
- Bitmap enumeration reads committed slot data. It cannot see uncommitted snapshot
  writes. Keep occupancy and physics-key masks current before using their helpers.
- Enumerators return values or references to borrowed storage; they do not own it.

## Current required-update limitation

In this revision, the `includeEmpty: true` branch advances brick coordinates but
does not refresh `sectorBrickIndex`. Its `Current.brickIdx` is therefore not a
usable compact ID in that mode. Use the yielded position and coordinate-based
accessors. The current `CollectBrickJob` copies that ID and uses it to read flag
arrays, so its cached `BrickInfo.BrickId`, `_BrickDirtyFlag`, and
`BrickRequireUpdateFlag` must not be relied on to identify or filter a brick in
new examples. Resolve the absolute index from `BrickOrigin` when a flag is needed.
This is an implementation gap found during documentation review, not fixed here.

## Other iteration APIs found in the package family

Physics adds `Caelix.Simulation.BrickOverlapEnumerator`. Obtain it with
`graph.TryGetOverlaps(source, out var neighbors)` and iterate `neighbors` with
`foreach`. Each result is a `BrickOverlapKey`: an entity GUID plus an entity-local
brick coordinate including sector offsets. An overlap is conservative and does
not prove that occupied voxels intersect. Re-fetch the graph each step and check
its version; the view borrows builder storage that is reused on the second next
publication. Do not dispose the borrowed arrays. See
[BrickOverlapGraph](https://github.com/betairylia/Caelix-physics/blob/main/Caelix.Physics/Runtime/Physics/BrickOverlapGraph.cs).

`SharedHashMap<TKey,TValue>.GetEnumerator()` returns Unity's
`UnsafeHashMap<TKey,TValue>.Enumerator`; use `foreach` for sector maps. Caelix also
uses standard collection enumerators. Core's private `WorldSaver.EnumerateSectors`
yields save records, and Caelix Editor's private `EnumerateInstanceMethods` yields
reflection results. They are not voxel traversal APIs.

The Physics fork additionally contains `VoxelContactEvents.Enumerator`,
`CollisionEvents.Enumerator`, `TriggerEvents.Enumerator`, `ImpulseEvents.Enumerator`,
`BlobArray.Accessor<T>.Enumerator`, and `ElementPoolBase.IndexEnumerator<T>` /
`ElementPoolBase.ElementEnumerator<T>`. These enumerate event streams or upstream
containers, rather than voxel coordinates. Consult their owner types for lifetime
and job scheduling rules. This inventory covers named enumerator declarations in
Core `Runtime`, Caelix `Runtime`, and both Physics `Caelix.Physics/Runtime` and
`Unity.Physics`, plus iterator methods in Caelix/Core; it excludes
third-party package caches, generated state machines, and test coroutines.

Physics owner sources: [voxel contacts](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Dynamics/Simulation/VoxelContactEvent.cs),
[collisions](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Dynamics/Simulation/CollisionEvent.cs),
[triggers](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Dynamics/Simulation/TriggerEvent.cs),
[impulses](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Dynamics/Simulation/ImpulseEvent.cs),
[blob arrays](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Base/Containers/BlobArray.cs),
and [element pools](https://github.com/betairylia/Caelix-physics/blob/main/Unity.Physics/Base/Containers/ElementPool.cs).

## Source and checks

- [Allocated bricks](../../Runtime/SectorNonEmptyBrickEnumerator.cs)
- [Required-update and dirty bricks](../../Runtime/SectorHelpers.cs)
- [Sector and brick bitmap enumerators](../../Runtime/SectorBitmaskSlotEnumerator.cs)
- [SharedHashMap](../../Runtime/SharedHashMap.cs)
- [Enumerator and bitmap tests](../../Tests/Editor/SectorStorageTests.cs)

Inventory search: `rg -n '(struct|class) .*Enumerator|GetEnumerator\(|yield return'`
over the package runtime sources. Recheck it when adding traversal APIs.

The C# example compiled against local Unity validation assemblies during this
documentation pass. It was not executed in Unity.

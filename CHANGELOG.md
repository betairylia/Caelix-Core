# Changelog

## [0.2.0] - 2026-10-31

- Brick-key facade: `VoxelEntityData` is the storage API (`BrickKey`, `TryBindBrick`, `BrickCursor`, `EnumerateBricks`, `OpenNeighborhood`, `VoxelRegion`, `MarkRequired`, `CollectRequiredBricks`) with a per-cycle change list; `Sector*` types are internal to Caelix-Core.
- Keyed replication: `BrickBatch` records, `BrickBatchApplier`, entity-level record codec; zero-slot Update records are skipped before they create work.
- Dirty propagation without managed scratch allocation; faster automata reads, snapshot storage and local message buffers; `SparseBrickIdTable` takes a slot count.
- Packed scene colour encoding and preview support preserved (`CAELIX_PACKED_SCENE_COLOR`).
- Storage and enumerator guides revised.
- License: GPL-3.0 with an additional permission (README, "License") for combining with Unity and the Caelix packages.

## [0.0.1-dev.1]

- Split the core runtime from the original VoxelisX package.
- Rename the API and namespaces to Caelix.
- Add the `.cxw` save format while retaining legacy `.vxw` read support.
- Save format v7: a world record (player pose) precedes the entity table; older files read as no pose.
- Require-update bits 0-11 are application automata channels (`Automata0`..`Automata11`); `GeneralAutomata` is gone. Voxel setters gain explicit-mask overloads; setters without a mask consult the application's `AutomataDefaults` tables. `AutomataBrick.SetDirty` schedules a brick without a write. `RequiredBrick.IsAllocated`; `BeginAutomataWrites(mask)`.
- Save format v8: no layout change. Pending bit 0 in older files is converted to `AutomataDefaults.LegacyGeneralAutomataFlags` on load.

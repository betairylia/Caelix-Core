# Changelog

## [0.0.1-dev.1]

- Split the core runtime from the original VoxelisX package.
- Rename the API and namespaces to Caelix.
- Add the `.cxw` save format while retaining legacy `.vxw` read support.
- Save format v7: a world record (player pose) precedes the entity table; older files read as no pose.
- Require-update bits 0-11 are application automata channels (`Automata0`..`Automata11`); `GeneralAutomata` is gone. Voxel setters gain explicit-mask overloads; setters without a mask consult the application's `AutomataDefaults` tables. `AutomataBrick.SetDirty` schedules a brick without a write. `RequiredBrick.IsAllocated`; `BeginAutomataWrites(mask)`.
- Save format v8: no layout change. Pending bit 0 in older files is converted to `AutomataDefaults.LegacyGeneralAutomataFlags` on load.

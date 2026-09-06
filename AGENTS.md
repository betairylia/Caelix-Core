# Caelix Core contributor rules

Read [the documentation index](Documentation~/index.md) before changing an
unfamiliar subsystem. Follow the linked shared documentation guide when writing docs.

- Use the existing voxel enumerators. Do not add raw sweeps over the 4,096 brick
  positions to discover allocated, dirty, or required-update bricks. See the
  [enumerator rule, inventory, and current limitations](Documentation~/reference/enumerators.md).
- Read [the storage contract](Documentation~/internals/voxel-storage.md) before
  changing slots, IDs, allocation, snapshots, or ownership. Struct copies can
  share native storage; complete users before freeing or replacing it.
- Keep `dirty` (source edits) distinct from `requireUpdate` (scheduled work).
- Update the owning documentation and relevant existing Editor tests when changing
  storage, topology, snapshots, propagation, or channel behavior. Report which
  checks ran; source review is not a Unity test result.

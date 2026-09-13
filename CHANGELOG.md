# Changelog

## [0.0.1-dev.1]

- Split the core runtime from the original VoxelisX package.
- Rename the API and namespaces to Caelix.
- Add the `.cxw` save format while retaining legacy `.vxw` read support.
- Save format v7: a world record (player pose) precedes the entity table; older files read as no pose.

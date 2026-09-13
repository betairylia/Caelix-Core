using Unity.Collections;
using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix.IO
{
    /// <summary>
    /// Receives entities materialized by <see cref="WorldLoader"/>. The target allocates the
    /// entity record (a world does this by creating the entity) and gets it back, filled, in
    /// <see cref="CommitEntity"/>.
    /// </summary>
    public interface IWorldLoadTarget
    {
        /// <summary>
        /// Creates the entity for <paramref name="record"/>. Return false to skip it. The returned
        /// data must have created sector maps; the loader adds sectors into them.
        /// </summary>
        bool TryCreateEntity(in EntityRecord record, out VoxelEntityData data);

        /// <summary>
        /// Called after the loader filled <paramref name="data"/> (sectors, transform, flags).
        /// Copy the scalar fields back into the owning store here.
        /// </summary>
        void CommitEntity(in EntityRecord record, in VoxelEntityData data);
    }

    /// <summary>
    /// Top-level load orchestrator. Reads entity records and per-sector payloads from an
    /// <see cref="IWorldSaveReader"/>, materializes <see cref="Sector"/> instances, and attaches
    /// them to entities created by an <see cref="IWorldLoadTarget"/>.
    ///
    /// On load the saved <c>requireUpdate</c> state is restored verbatim, then renderer +
    /// geometry bits (<c>BrickAdded | GeometryWithLocalNeighbor | Geometry</c>) are OR'd
    /// onto every non-empty brick so the first tick uploads geometry — without spuriously
    /// firing automata work that wasn't pending pre-save.
    /// </summary>
    public static class WorldLoader
    {
        public const ushort FirstFrameUploadFlags =
            (ushort)(BrickUpdateFlags.BlockBrickAdded | BrickUpdateFlags.GeometryWithLocalNeighbor | BrickUpdateFlags.Geometry);

        /// <summary>
        /// Loads every entity into <paramref name="target"/> and returns the file's world-level
        /// record. The record is not applied to anything: the caller decides what to do with the
        /// player pose (<see cref="WorldRecord.None"/> for files older than v7).
        /// </summary>
        public static WorldRecord Load(string path, IWorldLoadTarget target)
        {
            using var reader = SingleFileSaveStorage.OpenRead(path);
            for (int i = 0; i < reader.EntityCount; i++)
            {
                var rec = reader.ReadEntityRecord(i);
                if (!target.TryCreateEntity(in rec, out VoxelEntityData data)) continue;
                LoadEntity(reader, i, in rec, ref data);
                target.CommitEntity(in rec, in data);
            }
            return reader.World;
        }

        public static unsafe void LoadEntity(
            IWorldSaveReader reader, int entityIndex, in EntityRecord rec, ref VoxelEntityData data)
        {
            var transform = new RigidTransform(rec.Transform.Rotation, rec.Transform.Position);
            data.transform = transform;
            data.previousTransform = transform;
            data.linearVelocity = float3.zero;
            data.angularVelocity = float3.zero;
            data.isStatic = rec.IsStatic;
            data.isProtected = rec.Protected;

            var sectorIndex = reader.ReadSectorIndex(entityIndex);
            for (int s = 0; s < sectorIndex.Count; s++)
            {
                var entry = sectorIndex[s];
                byte[] payload = reader.ReadPayload(entityIndex, entry.Coord);
                var sector = SectorSerializer.Unpack(payload, Allocator.Persistent);
                data.ShallowCopyAndAddSectorAt(entry.Coord, sector);
            }

            data.RefreshAllocatedBrickLists();
            ApplyFirstFrameUploadFlags(in data);
            data.entityRequireUpdateFlags |= rec.EntityRequireUpdateFlags;
            // Also fold in the first-frame upload bits at entity level for consistency with sector aggregates.
            data.entityDirtyFlags |= FirstFrameUploadFlags;
        }

        // Accumulating the dirty flags so renderers can pick them up after dirty propagation
        private static unsafe void ApplyFirstFrameUploadFlags(in VoxelEntityData data)
        {
            foreach (var kvp in data.sectors)
            {
                ref Sector sector = ref kvp.Value.Get();
                foreach (SectorNonEmptyBrickEnumerator.BrickRef brickRef in sector.EnumerateNonEmptyBricks())
                {
                    sector.brickDirtyFlags[brickRef.BrickAbs] |= FirstFrameUploadFlags;
                }
                sector.sectorDirtyFlags |= FirstFrameUploadFlags;
            }
        }
    }
}

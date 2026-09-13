using System.Collections.Generic;
using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix.IO
{
    /// <summary>
    /// One entity as handed to the saver. Everything the file needs lives on
    /// <see cref="VoxelEntityData"/> except body presence and velocity, which the physics side
    /// supplies because they live on the body record.
    /// </summary>
    public readonly struct EntitySaveRecord
    {
        public readonly Guid128 Guid;
        public readonly VoxelEntityData Data;
        public readonly bool HasBody;
        public readonly float3 LinearVelocity;
        public readonly float3 AngularVelocity;

        public EntitySaveRecord(
            Guid128 guid,
            VoxelEntityData data,
            bool hasBody = false,
            float3 linearVelocity = default,
            float3 angularVelocity = default)
        {
            Guid = guid;
            Data = data;
            HasBody = hasBody;
            LinearVelocity = linearVelocity;
            AngularVelocity = angularVelocity;
        }
    }

    /// <summary>
    /// Top-level save orchestrator. Walks entity records, serializes each entity's sectors
    /// (preview + Deflate-compressed payload), and writes the resulting <c>.cxw</c> file.
    /// Record-based: no Unity components are involved, so a headless server can save.
    /// </summary>
    public static class WorldSaver
    {
        public static void Save(string path, IReadOnlyList<EntitySaveRecord> entities)
        {
            Save(path, entities, WorldRecord.None);
        }

        /// <summary>
        /// Saves the entities plus a world-level record (player pose and future world data).
        /// </summary>
        public static void Save(string path, IReadOnlyList<EntitySaveRecord> entities, in WorldRecord world)
        {
            using var writer = SingleFileSaveStorage.OpenWrite(path);
            writer.WriteWorld(in world);
            for (int i = 0; i < entities.Count; i++)
            {
                EntitySaveRecord rec = entities[i];
                if (rec.Data.excludeFromSave) continue;
                SaveEntity(writer, in rec);
            }
            writer.Commit();
        }

        /// <summary>
        /// Writes one entity. Staticness and protection are read straight off the entity's own
        /// data so a caller cannot persist a value that disagrees with the running entity.
        /// </summary>
        public static unsafe void SaveEntity(IWorldSaveWriter writer, in EntitySaveRecord rec)
        {
            VoxelEntityData data = rec.Data;
            var transformRec = new EntityTransformRecord(data.transform.pos, data.transform.rot);

            EntityFlags flags = EntityFlags.None;
            if (rec.HasBody) flags |= EntityFlags.HasBody;
            if (data.isStatic) flags |= EntityFlags.Static;

            var entityRecord = new EntityRecord(
                rec.Guid, transformRec, data.entityRequireUpdateFlags, flags,
                rec.LinearVelocity, rec.AngularVelocity, data.isProtected);
            writer.WriteEntity(in entityRecord, EnumerateSectors(data));
        }

        private static IEnumerable<SectorWriteRecord> EnumerateSectors(VoxelEntityData data)
        {
            foreach (var kvp in data.sectors)
            {
                yield return BuildSectorWriteRecord(kvp.Key, kvp.Value);
            }
        }

        private static unsafe SectorWriteRecord BuildSectorWriteRecord(int3 coord, SectorHandle sector)
        {
            uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
            PreviewBuilder.Build(sector.Ptr, preview);
            ref Sector source = ref sector.Get();
            byte[] payload = SectorSerializer.Pack(in source);
            return new SectorWriteRecord(coord, preview, payload);
        }
    }
}

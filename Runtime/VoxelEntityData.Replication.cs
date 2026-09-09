using Unity.Mathematics;
using Caelix.Net;

namespace Caelix
{
    /// <summary>
    /// The replication half of the brick-key facade: sizing and writing one keyed brick record,
    /// and publishing a removal the applier performed. A record names its brick by
    /// <see cref="BrickKey">key</see>, so neither the packer nor the receiver learns which sector
    /// it falls into.
    /// </summary>
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>
        /// Exact bytes <see cref="WriteReplicatedRecord"/> emits for that key and op. A Remove
        /// record is the header alone; an Update record adds the brick's slot records, or a single
        /// zero count byte when the brick is not allocated.
        /// </summary>
        /// <param name="key">Entity-local brick key.</param>
        /// <param name="op">What the record does to the brick at <paramref name="key"/>.</param>
        /// <param name="slotMask">Slots to replicate, one bit per slot id.</param>
        public int ReplicatedRecordBytes(int3 key, BrickOp op, ushort slotMask)
        {
            if (op == BrickOp.Remove)
            {
                return BrickReplication.RecordHeaderBytes;
            }

            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return BrickReplication.RecordHeaderBytes + 1;
            }

            ref Sector sector = ref handle.Get();
            if (sector.brickIdx[SectorKeys.ToBrickIdx(key)] == Sector.BRICKID_EMPTY)
            {
                return BrickReplication.RecordHeaderBytes + 1;
            }

            return BrickReplication.RecordHeaderBytes + sector.ReplicatedBrickBytes(slotMask);
        }

        /// <summary>
        /// Writes one record: key, op, and for an Update the slot records of the brick (a zero
        /// count when it is not allocated). The writer must have
        /// <see cref="ReplicatedRecordBytes"/> bytes of room.
        /// </summary>
        /// <remarks>
        /// Burst-callable. The caller owns the phase rule: nothing may mutate this entity between
        /// the size call and the write, or the two disagree and the writer overruns.
        /// </remarks>
        public void WriteReplicatedRecord(ref UnsafeNetWriter writer, int3 key, BrickOp op, ushort slotMask)
        {
            writer.Write(key);
            writer.Write((byte)op);
            if (op == BrickOp.Remove)
            {
                return;
            }

            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                writer.Write((byte)0);
                return;
            }

            // WriteReplicatedBrick writes the zero count itself for an unallocated brick.
            handle.Get().WriteReplicatedBrick(ref writer, SectorKeys.ToBrickIdx(key), slotMask);
        }

        /// <summary>
        /// Appends one <see cref="ChangeKind.Removed"/> entry for a brick the applier freed. Same
        /// entry shape as the sector-wide removal path, so a consumer cannot tell the two apart.
        /// </summary>
        internal void AppendRemovedBrick(int3 key)
        {
            if (changeLog == null)
            {
                return;
            }

            changeLog->Entries.Add(new BrickChange
            {
                Key = key,
                Kind = ChangeKind.Removed,
                SourceFlags = DirtyFlags.BlockBrickRemoved,
                RequiredFlags = DirtyFlags.BlockBrickRemoved
            });
        }
    }
}

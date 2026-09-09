using Unity.Collections.LowLevel.Unsafe;

namespace Caelix.Net
{
    /// <summary>What a brick record does to the brick at its key.</summary>
    public enum BrickOp : byte
    {
        /// <summary>Writes the record's slot bytes into the brick, allocating it when it is missing.</summary>
        Update = 0,

        /// <summary>Frees the brick at the key. A Remove record carries no slot records.</summary>
        Remove = 1,
    }

    /// <summary>
    /// Validation result of one BrickBatch message. Returned per message by
    /// <see cref="BrickBatchApplier"/>.
    /// </summary>
    public enum ReplicatedBrickBatchError : byte
    {
        None,
        Truncated,
        InvalidOp,
        InvalidSlot,
        SlotStrideMismatch,
    }

    /// <summary>
    /// Constants of the brick replication protocol. Moved here from <c>Sector</c>: nothing outside
    /// Caelix-Core names a sector.
    /// </summary>
    public static class BrickReplication
    {
        /// <summary>
        /// Dirty bits that mean "the Block slot of this brick changed this cycle". The Block slot is
        /// the only writer that sets <see cref="DirtyFlags.Geometry"/>.
        /// </summary>
        public const DirtyFlags ReplicationDirtyMask =
            DirtyFlags.Geometry | DirtyFlags.BlockBrickAdded | DirtyFlags.BlockBrickRemoved;

        /// <summary>
        /// Dirty bits the client marks on an applied brick so its renderer refreshes. These bits
        /// cannot allocate sectors during propagation (see <see cref="DirtyPropagationSettings"/>).
        /// </summary>
        public const DirtyFlags ReplicationApplyFlags =
            DirtyFlags.Geometry | DirtyFlags.GeometryWithLocalNeighbor;

        /// <summary>
        /// Dirty bits a removed brick's index keeps so propagation re-renders its 26 neighbours.
        /// <see cref="DirtyFlags.GeometryWithLocalNeighbor"/> is the one that travels; it cannot
        /// allocate storage, so the mark never grows a phantom sector.
        /// </summary>
        public const DirtyFlags ReplicationRemoveFlags =
            DirtyFlags.BlockBrickRemoved | DirtyFlags.GeometryWithLocalNeighbor;

        /// <summary>The default replicated slot mask: Block only.</summary>
        public const ushort DefaultReplicatedSlotMask = 1 << (int)SectorSlotId.Block;

        /// <summary>Slot mask bit for a slot id.</summary>
        public static ushort SlotBit(SectorSlotId slotId) => (ushort)(1 << (int)slotId);

        /// <summary>Bytes of a record before its slot records: <c>int3 key</c> plus <c>u8 op</c>.</summary>
        public const int RecordHeaderBytes = 12 + 1;

        /// <summary>Bytes of a slot record before its voxel bytes: <c>u8 slotId</c> plus <c>u16 stride</c>.</summary>
        public const int SlotRecordHeaderBytes = 1 + 2;

        /// <summary>
        /// Reads one value from a wire record. Records need not be aligned, so this matches
        /// <c>NetMessageReader</c>'s native-endian, unaligned codec.
        /// </summary>
        internal static unsafe T ReadUnaligned<T>(byte* source) where T : unmanaged
        {
            UnsafeUtility.CopyPtrToStructure(source, out T value);
            return value;
        }
    }
}

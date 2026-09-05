using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Caelix.Net;

namespace Caelix
{
    /// <summary>
    /// Brick-level replication codec. The server packs the replicated slots of one brick; the
    /// client applies them raw. Records carry slot id and stride, so the receiving side never
    /// needs the C# type of a slot. Layout per brick:
    /// <code>
    ///   u8  slotCount
    ///   repeated: u8 slotId, u16 stride, byte[Sector.BLOCKS_IN_BRICK * stride]
    /// </code>
    /// </summary>
    public unsafe partial struct Sector
    {
        /// <summary>
        /// Dirty bits that mean "the Block slot of this brick changed this tick". The Block slot is
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

        /// <summary>Slot mask bit for a slot id.</summary>
        public static ushort SlotBit(SectorSlotId slotId) => (ushort)(1 << (int)slotId);

        /// <summary>The default replicated slot mask: Block only.</summary>
        public const ushort DefaultReplicatedSlotMask = 1 << (int)SectorSlotId.Block;

        /// <summary>
        /// Exact number of bytes <see cref="WriteReplicatedBrick"/> emits for one ALLOCATED brick
        /// under <paramref name="slotMask"/>: the slot-count byte plus, per masked slot that exists
        /// in this sector, its id byte, its stride field and one brick of voxel data.
        /// </summary>
        /// <remarks>
        /// The value is a property of the SECTOR, not of a brick: it depends only on which slots
        /// exist and how wide they are, and every allocated brick writes exactly this much. So a
        /// caller sizing a buffer for a batch computes it once and multiplies by the brick count. An
        /// unallocated brick writes one byte instead (the zero count), which this bound covers.
        /// </remarks>
        public int ReplicatedBrickBytes(ushort slotMask)
        {
            int bytes = 1; // u8 slotCount
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if ((slotMask & (1 << i)) == 0) continue;
                SectorSlotStorage* slot = slots + i;
                if (!slot->IsCreated) continue;

                bytes += 1 + 2 + slot->BytesPerBrick; // u8 slotId, u16 stride, byte[BLOCKS_IN_BRICK * stride]
            }

            return bytes;
        }

        /// <summary>
        /// Writes the replicated slots of the brick at <paramref name="brickIdx"/>. Slots that are
        /// not in <paramref name="slotMask"/> or not created in this sector are skipped. Writes a
        /// zero count for an unallocated brick. The writer must already have
        /// <see cref="ReplicatedBrickBytes"/> bytes of room left.
        /// </summary>
        public void WriteReplicatedBrick(ref UnsafeNetWriter writer, int brickIdx, ushort slotMask)
        {
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                writer.Write((byte)0);
                return;
            }

            int countOffset = writer.Reserve<byte>();
            byte count = 0;
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if ((slotMask & (1 << i)) == 0) continue;
                SectorSlotStorage* slot = slots + i;
                if (!slot->IsCreated) continue;

                writer.Write((byte)i);
                writer.Write((ushort)slot->stride);
                writer.WriteBytes(slot->GetBrickPtr(bid), slot->BytesPerBrick);
                count++;
            }

            writer.Patch(countOffset, count);
        }

        /// <summary>
        /// Applies one brick written by <see cref="WriteReplicatedBrick"/>. Allocates the brick and
        /// any missing slot storage, copies the raw bytes, marks the brick dirty with
        /// <see cref="ReplicationApplyFlags"/> (plus <see cref="DirtyFlags.BlockBrickAdded"/> for a
        /// brick this sector did not have yet), and widens the block AABB to the brick bounds.
        /// Main thread only; the client replica is never written from jobs.
        /// </summary>
        public void ApplyReplicatedBrick(int brickIdx, ref NetMessageReader reader)
        {
            int count = reader.Read<byte>();
            if (count == 0)
            {
                return;
            }

            int3 brickPos = ToBrickPos((short)brickIdx);
            short bid = brickMap.indices[brickIdx];
            bool isNew = bid == BRICKID_EMPTY;
            if (isNew)
            {
                bid = AllocateBrick(ref brickMap, slots, brickIdx, brickPos);
            }

            for (int rec = 0; rec < count; rec++)
            {
                int slotId = reader.Read<byte>();
                int stride = reader.Read<ushort>();
                int bytes = stride * BLOCKS_IN_BRICK;

                if (slotId >= MAX_SLOTS || stride <= 0)
                {
                    throw new InvalidDataException($"Invalid replicated slot record: id {slotId}, stride {stride}.");
                }

                SectorSlotStorage* slot = EnsureSlotStorage(
                    slots, slotId, stride, brickMap.Capacity, _allocator, 0);
                reader.ReadBytes(slot->GetBrickPtr(bid), bytes);
            }

            DirtyFlags flags = ReplicationApplyFlags | (isNew ? DirtyFlags.BlockBrickAdded : DirtyFlags.None);
            MarkBrickDirty(brickIdx, flags, 0xFFFFFFFF);

            // Widened to the whole brick: blockAABB is a grow-only upper bound (see its field doc).
            int3 origin = brickPos * SIZE_IN_BLOCKS;
            blockAABB.Update(origin);
            blockAABB.Update(origin + new int3(SIZE_IN_BLOCKS - 1));
        }

        /// <summary>
        /// Writes one voxel's values for the slots in <paramref name="slotMask"/> that exist:
        /// <c>u8 count</c> then <c>u8 slotId, u16 stride, byte[stride]</c> per record.
        /// </summary>
        public void WriteVoxelSlots(NetMessageWriter writer, int x, int y, int z, ushort slotMask)
        {
            ResolveVoxel(x, y, z, out int brickIdx, out int voxelIdx);
            short bid = brickMap.indices[brickIdx];

            int countOffset = writer.Reserve<byte>();
            byte count = 0;
            for (int i = 0; i < MAX_SLOTS; i++)
            {
                if ((slotMask & (1 << i)) == 0) continue;
                SectorSlotStorage* slot = slots + i;
                if (!slot->IsCreated) continue;

                writer.Write((byte)i);
                writer.Write((ushort)slot->stride);
                if (bid == BRICKID_EMPTY)
                {
                    // Unallocated brick reads as default for every slot.
                    for (int b = 0; b < slot->stride; b++) writer.Write((byte)0);
                }
                else
                {
                    writer.WriteBytes(slot->GetVoxelPtr(bid, voxelIdx), slot->stride);
                }

                count++;
            }

            writer.Patch(countOffset, count);
        }
    }
}

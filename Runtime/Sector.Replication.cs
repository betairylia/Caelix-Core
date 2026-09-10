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
    /// The keyed record that wraps this (key, op, then these bytes) is written by
    /// <see cref="VoxelEntityData.WriteReplicatedRecord"/> and applied by
    /// <see cref="BrickBatchApplier"/>; the members here are the per-brick primitives those two
    /// use, and are internal because nothing outside Caelix-Core names a sector.
    /// </summary>
    internal unsafe partial struct Sector
    {
        /// <summary>
        /// Exact number of bytes <see cref="WriteReplicatedBrick"/> emits for one ALLOCATED brick
        /// under <paramref name="slotMask"/>: the slot-count byte plus, per masked slot that exists
        /// in this sector, its id byte, its stride field and one brick of voxel data.
        /// </summary>
        /// <remarks>
        /// The value is a property of the SECTOR, not of a brick: it depends only on which slots
        /// exist and how wide they are, and every allocated brick writes exactly this much. An
        /// unallocated brick writes one byte instead (the zero count), which this bound covers.
        /// </remarks>
        internal int ReplicatedBrickBytes(ushort slotMask)
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
        internal void WriteReplicatedBrick(ref UnsafeNetWriter writer, int brickIdx, ushort slotMask)
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
        /// Applies one VALIDATED Update record's slot records to the brick at
        /// <paramref name="brickIdx"/>: allocates the brick and any missing slot storage, copies
        /// the raw bytes, marks the brick dirty with
        /// <see cref="BrickReplication.ReplicationApplyFlags"/> (plus
        /// <see cref="DirtyFlags.BlockBrickAdded"/> for a brick this sector did not have yet), and
        /// widens the block AABB to the brick bounds.
        /// </summary>
        /// <param name="brickIdx">Absolute brick index inside this sector.</param>
        /// <param name="slotRecords">First slot record; the caller has already read the count byte.</param>
        /// <param name="slotCount">Number of slot records, at least one.</param>
        /// <returns>True when the brick was allocated by this call.</returns>
        /// <remarks>
        /// Bounds, slot ids and strides are the applier's job: this primitive trusts its input.
        /// The caller owns the input memory and exclusive access to this sector until the call
        /// finishes. Different sectors may be applied in parallel; records for the same sector
        /// must stay in receive order. Refresh <see cref="UpdateNonEmptyBricks"/> after the last
        /// call that changed allocation.
        /// </remarks>
        internal bool ApplyReplicatedBrick(int brickIdx, byte* slotRecords, int slotCount)
        {
            int3 brickPos = ToBrickPos((short)brickIdx);
            short bid = brickMap.indices[brickIdx];
            bool isNew = bid == BRICKID_EMPTY;
            if (isNew)
            {
                bid = AllocateBrick(ref brickMap, slots, brickIdx, brickPos);
            }

            int offset = 0;
            for (int s = 0; s < slotCount; s++)
            {
                int slotId = slotRecords[offset];
                int stride = BrickReplication.ReadUnaligned<ushort>(slotRecords + offset + 1);
                offset += BrickReplication.SlotRecordHeaderBytes;

                SectorSlotStorage* slot = EnsureSlotStorage(
                    slots, slotId, stride, brickMap.Capacity, _allocator, 0);
                int bytes = stride * BLOCKS_IN_BRICK;
                UnsafeUtility.MemCpy(slot->GetBrickPtr(bid), slotRecords + offset, bytes);
                offset += bytes;
            }

            MarkBrickDirty(
                brickIdx,
                BrickReplication.ReplicationApplyFlags | (isNew ? DirtyFlags.BlockBrickAdded : DirtyFlags.None),
                0xFFFFFFFF);

            int3 origin = brickPos * SIZE_IN_BLOCKS;
            blockAABB.Update(origin);
            blockAABB.Update(origin + new int3(SIZE_IN_BLOCKS - 1));
            return isNew;
        }

        /// <summary>
        /// Frees the brick at <paramref name="brickIdx"/>. Returns false when it was not allocated.
        /// </summary>
        /// <remarks>
        /// The index keeps <see cref="BrickReplication.ReplicationRemoveFlags"/> dirty with every
        /// direction set, so the next propagation re-renders the 26 neighbours: propagation reads
        /// the flag arrays BY INDEX and never asks whether the brick still exists.
        /// </remarks>
        internal bool RemoveReplicatedBrick(int brickIdx)
        {
            if (brickMap.RemoveBrick(ToBrickPos((short)brickIdx)) == SparseBrickIdTable.EMPTY)
            {
                return false;
            }

            MarkBrickDirty(brickIdx, BrickReplication.ReplicationRemoveFlags, 0xFFFFFFFF);
            return true;
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

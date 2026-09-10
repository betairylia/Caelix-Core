using System;
using Unity.Mathematics;

namespace Caelix
{
    // TODO: Lazy resolve except center?
    /// <summary>
    /// One brick of automata work: reads over the 27-brick window, writes into the center brick.
    /// </summary>
    /// <remarks>
    /// ENTITY-LOCAL block coordinates throughout — no sector frame appears in this API.
    ///
    /// The phase rule: reads see the FRONT buffer, which holds the tick's start state, and writes
    /// land in the PENDING buffer that <see cref="VoxelEntityData.EndAutomataWrites"/> swaps in. A
    /// value written through this brick is therefore not visible to any read until the stage ends,
    /// which is what makes a cellular rule over a neighbourhood well defined. Open one with
    /// <see cref="VoxelEntityData.OpenAutomataBrick"/> after
    /// <see cref="VoxelEntityData.BeginAutomataWrites"/>, and drop it before the swap.
    ///
    /// Reads span <c>Origin - 8 .. Origin + 15</c> per axis: the center brick plus one block past
    /// each face. Writes are restricted to the center brick, which is what makes bricks
    /// independent and the stage parallel.
    /// </remarks>
    public unsafe struct AutomataBrick
    {
        /// <summary>Entity-local brick key of the center brick.</summary>
        public int3 Key;

        /// <summary>Entity-local block position of the center brick's first block.</summary>
        public int3 Origin;

        // Front-buffer Block* per neighbour brick; null where a brick is not allocated.
        private VoxelNeighborhood blocks;

        // Storage of the region that owns the center brick, and its neighbouring regions. Both are
        // plumbing for the non-Block slot reads and for the write path; neither is exposed.
        private SectorHandle center;
        private SectorNeighborHandles neighbors;
        private int3 centerSectorPos;

        /// <summary>Builds an access record. Internal: only the facade knows how to resolve storage.</summary>
        internal static AutomataBrick Create(
            int3 key, int3 centerSectorPos, SectorHandle center, SectorNeighborHandles neighbors,
            VoxelNeighborhood blocks)
        {
            return new AutomataBrick
            {
                Key = key,
                Origin = BrickKey.ToBlockOrigin(key),
                blocks = blocks,
                center = center,
                neighbors = neighbors,
                centerSectorPos = centerSectorPos
            };
        }

        /// <summary>True when the center brick holds storage. Writes work either way; they allocate.</summary>
        public bool IsAllocated => blocks.Center != null;

        /// <summary>
        /// The block at an entity-local <paramref name="blockPos"/> in the read window, from the
        /// FRONT buffer. <c>default</c> where the brick is not allocated.
        /// </summary>
        public Block GetBlock(int3 blockPos) => blocks.GetBlock(blockPos);

        /// <summary>
        /// One slot value at an entity-local <paramref name="blockPos"/> in the read window, from
        /// the FRONT buffer. <c>default</c> where the brick, the region or the slot has no storage.
        /// </summary>
        public T GetSlot<T>(SectorSlotId slot, int3 blockPos) where T : unmanaged
        {
            int3 sectorLocal = blockPos - centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
            bool inside = math.all(sectorLocal >= 0) &&
                          math.all(sectorLocal < Sector.SECTOR_SIZE_IN_BLOCKS);

            if (inside)
            {
                return center.IsNull
                    ? default
                    : center.GetSlot<T>(slot, sectorLocal.x, sectorLocal.y, sectorLocal.z);
            }

            if (neighbors.Neighbors == null)
            {
                return default;
            }

            return new SectorNeighborhoodReaderHelper(center, neighbors).GetSlot<T>(slot, sectorLocal);
        }

        /// <summary>
        /// Writes a block at an entity-local <paramref name="blockPos"/> inside the CENTER brick.
        /// The write lands in the pending buffer; see the phase rule on this type. Allocates the
        /// center brick when it holds no storage yet.
        /// </summary>
        public void SetBlock(int3 blockPos, Block block) => SetSlot(SectorSlotId.Block, blockPos, block);

        /// <summary>
        /// Writes one slot value at an entity-local <paramref name="blockPos"/> inside the CENTER
        /// brick. The write lands in the pending buffer; see the phase rule on this type.
        /// </summary>
        public void SetSlot<T>(SectorSlotId slot, int3 blockPos, T value)
            where T : unmanaged, IEquatable<T>
        {
            CheckInCenterBrick(blockPos);
            if (center.IsNull)
            {
                CheckHasCenter();
                return;
            }

            int3 sectorLocal = blockPos - centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS;
            center.SetSlot(slot, sectorLocal.x, sectorLocal.y, sectorLocal.z, value);
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckInCenterBrick(int3 blockPos)
        {
            int3 local = blockPos - Origin;
            if (math.any(local < 0) || math.any(local >= BrickKey.BlocksPerAxis))
            {
                throw new IndexOutOfRangeException(
                    "An automata brick writes only inside its own center brick.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckHasCenter()
        {
            throw new InvalidOperationException(
                "This automata brick has no region behind it; its writes are dropped.");
        }
    }
}

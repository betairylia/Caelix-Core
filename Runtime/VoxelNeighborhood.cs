using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Read window over the 27 bricks around one brick key, opened by
    /// <see cref="VoxelEntityData.OpenNeighborhood"/>.
    /// </summary>
    /// <remarks>
    /// Coordinates are ENTITY-LOCAL block positions. The valid range is
    /// <c>origin - 8 .. origin + 15</c> per axis, where <c>origin = BrickKey.ToBlockOrigin(key)</c>;
    /// a consumer that reads one block past a brick face therefore stays inside it. Unallocated
    /// bricks read as <c>default</c>. The bound pointers follow the phase rule: they stay valid
    /// until the next mutation of the entity's storage, and nothing re-validates them.
    /// </remarks>
    public unsafe struct VoxelNeighborhood : IBlockReader
    {
        // Block* per neighbour brick, index (dx+1) + (dy+1)*3 + (dz+1)*9; 0 = unallocated.
        private fixed long bricks[27];
        private int3 originBlock;

        /// <summary>Index of the centre brick in the 3x3x3 table.</summary>
        internal const int CenterIndex = 13;

        /// <summary>Sets the entity-local block origin of the centre brick.</summary>
        internal void SetOrigin(int3 origin)
        {
            originBlock = origin;
        }

        /// <summary>Stores one neighbour brick's storage pointer, or null when it is not allocated.</summary>
        internal void SetBrick(int index, void* data)
        {
            bricks[index] = (long)data;
        }

        /// <summary>Entity-local block position of the centre brick's first block.</summary>
        public int3 Origin => originBlock;

        /// <summary>Storage of the centre brick, or null when it is not allocated.</summary>
        public Block* Center => (Block*)bricks[CenterIndex];

        /// <summary>
        /// The block at an entity-local <paramref name="blockPos"/> inside the window, or
        /// <c>default</c> when its brick is not allocated.
        /// </summary>
        public Block GetBlock(int3 blockPos)
        {
            int3 rel = blockPos - originBlock;
            CheckInWindow(rel);

            int3 cell = (rel + BrickKey.BlocksPerAxis) >> BrickKey.Shift;   // 0..2
            Block* brick = (Block*)bricks[cell.x + cell.y * 3 + cell.z * 9];
            if (brick == null)
            {
                return default;
            }

            int3 local = rel & BrickKey.Mask;
            return brick[BrickKey.ToBlockIdx(local.x, local.y, local.z)];
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckInWindow(int3 rel)
        {
            if (math.any(rel < -BrickKey.BlocksPerAxis) || math.any(rel > 2 * BrickKey.BlocksPerAxis - 1))
            {
                throw new System.IndexOutOfRangeException(
                    "VoxelNeighborhood read outside its 27-brick window.");
            }
        }
    }
}

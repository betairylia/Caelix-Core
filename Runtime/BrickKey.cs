using System.Runtime.CompilerServices;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Brick keys are <see cref="int3"/> in entity-local BRICK units: <c>key = blockPos &gt;&gt; 3</c>.
    /// Backend-neutral: a key names a brick, never a sector, so nothing outside Caelix-Core has to
    /// know how bricks are grouped in storage.
    /// </summary>
    public static class BrickKey
    {
        /// <summary>Bits a block position is shifted right by to get its brick key. Equals <c>Sector.SHIFT_IN_BLOCKS</c>.</summary>
        public const int Shift = 3;

        /// <summary>Mask that extracts a block's position inside its brick (0-7 per axis).</summary>
        public const int Mask = (1 << Shift) - 1;

        /// <summary>Blocks along one axis of a brick.</summary>
        public const int BlocksPerAxis = 1 << Shift;

        /// <summary>Blocks in one brick (8 * 8 * 8 = 512).</summary>
        public const int BlocksInBrick = BlocksPerAxis * BlocksPerAxis * BlocksPerAxis;

        /// <summary>The key of the brick that contains <paramref name="blockPos"/>. Negative positions are handled.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 FromBlock(int3 blockPos) => blockPos >> Shift;

        /// <summary>Entity-local block position of a brick's first block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 ToBlockOrigin(int3 key) => key << Shift;

        /// <summary>Position of a block inside its own brick (0-7 per axis).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 LocalBlock(int3 blockPos) => blockPos & Mask;

        /// <summary>
        /// Flat index of a brick-local block position, x-fastest. The brick layout is part of the
        /// storage contract, so consumers outside Caelix-Core address a bound brick through this
        /// rather than through a sector helper.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBlockIdx(int x, int y, int z)
            => x + y * BlocksPerAxis + z * (BlocksPerAxis * BlocksPerAxis);
    }

    /// <summary>
    /// Conversions between brick keys and the sector backend. Internal to Caelix-Core: nothing
    /// outside it may know that a sector is 16 bricks wide.
    /// </summary>
    internal static class SectorKeys
    {
        /// <summary>Position of the sector that stores the brick at <paramref name="key"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 ToSectorPos(int3 key) => key >> Sector.SHIFT_IN_BRICKS;

        /// <summary>Absolute brick index of <paramref name="key"/> inside its own sector.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToBrickIdx(int3 key) => Sector.ToBrickIdx(
            key.x & Sector.SECTOR_MASK, key.y & Sector.SECTOR_MASK, key.z & Sector.SECTOR_MASK);

        /// <summary>The key of the brick at <paramref name="brickIdxAbs"/> in the sector at <paramref name="sectorPos"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int3 FromSector(int3 sectorPos, int brickIdxAbs)
            => (sectorPos << Sector.SHIFT_IN_BRICKS) + Sector.ToBrickPos((short)brickIdxAbs);
    }
}

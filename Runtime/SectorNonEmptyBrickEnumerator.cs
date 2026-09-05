using System.Runtime.CompilerServices;

namespace Caelix
{
    /// <summary>
    /// Walks a sector's allocated bricks, yielding each brick's absolute index and shared brick id.
    /// "Non-empty" follows the engine's historical naming here: an allocated brick may currently
    /// contain only empty blocks.
    /// </summary>
    /// <remarks>
    /// Reads no flag array — allocation alone selects a brick — so it is valid at any point in the
    /// tick. Burst-safe and foreach-able: it deliberately implements no <c>IEnumerator</c>
    /// interface, so <c>foreach</c> binds to these members directly with no boxing.
    ///
    /// The traversal is a plain linear sweep of the 4096-entry brick index table. That scan is the
    /// cost every caller pays and the single place to optimize it — an allocated-brick bitmask, say
    /// — because the two flag enumerators
    /// (<see cref="SectorRequiredUpdateBrickEnumerator"/>, <see cref="SectorDirtyBrickEnumerator"/>)
    /// and <see cref="SectorBitmaskSlotEnumerator{T}"/> are all built on it, and the backing can be
    /// swapped without touching any caller.
    ///
    /// Callers: the IO preview builder and world loader, the Block occupancy-mask refresh, the
    /// authoring tool (Caelix), the physics slot generation pass, the voxel collision manifold and
    /// the brick overlap query (Caelix.Physics).
    /// </remarks>
    public unsafe struct SectorNonEmptyBrickEnumerator
    {
        /// <summary>One allocated brick: its absolute index in the sector and its shared brick id.</summary>
        public readonly struct BrickRef
        {
            public readonly int BrickAbs;
            public readonly short Bid;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public BrickRef(int brickAbs, short bid)
            {
                BrickAbs = brickAbs;
                Bid = bid;
            }
        }

        private readonly short* indices;
        private int cursor;
        private short bid;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator(in Sector sector)
        {
            indices = sector.brickIdx;
            cursor = -1;
            bid = Sector.BRICKID_EMPTY;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator GetEnumerator() => this;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            cursor = -1;
            bid = Sector.BRICKID_EMPTY;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            for (int i = cursor + 1; i < Sector.BRICKS_IN_SECTOR; i++)
            {
                short b = indices[i];
                if (b != Sector.BRICKID_EMPTY)
                {
                    cursor = i;
                    bid = b;
                    return true;
                }
            }
            cursor = Sector.BRICKS_IN_SECTOR;
            return false;
        }

        public BrickRef Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new BrickRef(cursor, bid);
        }
    }

    public unsafe partial struct Sector
    {
        /// <summary>Iterates this sector's allocated bricks; see <see cref="SectorNonEmptyBrickEnumerator"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorNonEmptyBrickEnumerator EnumerateNonEmptyBricks()
            => new SectorNonEmptyBrickEnumerator(this);

        /// <summary>
        /// Iterates this sector's allocated bricks whose RAW dirty flags match
        /// <paramref name="mask"/>; see <see cref="SectorDirtyBrickEnumerator"/> for when reading the
        /// write side of the dirty contract is legitimate.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SectorDirtyBrickEnumerator EnumerateDirtyBricks(DirtyFlags mask)
            => new SectorDirtyBrickEnumerator(this, mask);
    }
}

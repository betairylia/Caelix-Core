using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Walks the allocated bricks of one entity whose key lies in an inclusive key range, yielding
    /// entity-local brick keys. Produced by <see cref="VoxelEntityData.EnumerateBricks(int3,int3)"/>.
    /// </summary>
    /// <remarks>
    /// Burst-compatible and foreach-able: it deliberately implements no <c>IEnumerator</c>
    /// interface, so <c>foreach</c> binds to these members directly with no boxing. It allocates
    /// nothing. Order is storage order — the sector map's order, then brick index order inside a
    /// sector — and is therefore not a spatial order; a consumer that needs one sorts for itself.
    /// </remarks>
    public unsafe struct BrickKeyEnumerator
    {
        private UnsafeHashMap<int3, SectorHandle>.Enumerator sectorEnumerator;
        private SectorNonEmptyBrickEnumerator brickEnumerator;

        private readonly int3 minKey;
        private readonly int3 maxKey;
        private readonly int3 minSector;
        private readonly int3 maxSector;

        private int3 currentSectorPos;
        private int3 current;
        private bool inSector;
        private bool finished;

        /// <summary>Builds an enumerator over the bricks of <paramref name="sectors"/> within a key range.</summary>
        /// <remarks>
        /// Sector positions are compared against the range's own sector bounds rather than the raw
        /// keys, so an unbounded range built from <see cref="int.MinValue"/> and
        /// <see cref="int.MaxValue"/> cannot overflow.
        /// </remarks>
        internal BrickKeyEnumerator(SharedHashMap<int3, SectorHandle> sectors, int3 minKey, int3 maxKey)
        {
            this.minKey = minKey;
            this.maxKey = maxKey;
            minSector = SectorKeys.ToSectorPos(minKey);
            maxSector = SectorKeys.ToSectorPos(maxKey);

            finished = !sectors.IsCreated || math.any(minKey > maxKey);
            sectorEnumerator = finished ? default : sectors.GetEnumerator();
            brickEnumerator = default;
            currentSectorPos = default;
            current = default;
            inSector = false;
        }

        /// <summary>Returns this enumerator (enables foreach usage).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BrickKeyEnumerator GetEnumerator() => this;

        /// <summary>The key of the brick the enumerator currently points at.</summary>
        public int3 Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>Advances to the next allocated brick inside the range.</summary>
        public bool MoveNext()
        {
            if (finished)
            {
                return false;
            }

            while (true)
            {
                if (inSector)
                {
                    while (brickEnumerator.MoveNext())
                    {
                        int3 key = SectorKeys.FromSector(currentSectorPos, brickEnumerator.Current.BrickAbs);
                        if (math.all(key >= minKey) && math.all(key <= maxKey))
                        {
                            current = key;
                            return true;
                        }
                    }

                    inSector = false;
                }

                if (!sectorEnumerator.MoveNext())
                {
                    finished = true;
                    return false;
                }

                int3 sectorPos = sectorEnumerator.Current.Key;
                if (math.any(sectorPos < minSector) || math.any(sectorPos > maxSector))
                {
                    continue;
                }

                SectorHandle handle = sectorEnumerator.Current.Value;
                if (handle.IsNull)
                {
                    continue;
                }

                currentSectorPos = sectorPos;
                brickEnumerator = handle.Get().EnumerateNonEmptyBricks();
                inSector = true;
            }
        }
    }
}

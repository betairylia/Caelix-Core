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
    ///
    /// A range that covers only a few regions is walked by LOOKUP instead of by scanning the whole
    /// region map, so enumerating one region of a six-thousand-region world costs one hash lookup,
    /// not six thousand skips. Only the order in which regions are visited differs between the two
    /// paths; the order inside a region, and the set of keys yielded, are identical.
    /// </remarks>
    public unsafe struct BrickKeyEnumerator
    {
        /// <summary>Region count at or below which the range is walked by lookup instead of by scan.</summary>
        private const long MaxLookupRegions = 64;

        private SharedHashMap<int3, SectorHandle> sectors;
        private UnsafeHashMap<int3, SectorHandle>.Enumerator sectorEnumerator;
        private SectorNonEmptyBrickEnumerator brickEnumerator;

        private readonly int3 minKey;
        private readonly int3 maxKey;
        private readonly int3 minSector;
        private readonly int3 maxSector;
        private readonly bool byLookup;

        private int3 lookupCursor;
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
            this.sectors = sectors;
            this.minKey = minKey;
            this.maxKey = maxKey;
            minSector = SectorKeys.ToSectorPos(minKey);
            maxSector = SectorKeys.ToSectorPos(maxKey);

            finished = !sectors.IsCreated || math.any(minKey > maxKey);

            // The extents are compared one axis at a time before the product, so a huge range can
            // never overflow the volume it is rejected by.
            long ex = (long)maxSector.x - minSector.x + 1;
            long ey = (long)maxSector.y - minSector.y + 1;
            long ez = (long)maxSector.z - minSector.z + 1;
            byLookup = !finished &&
                       ex <= MaxLookupRegions && ey <= MaxLookupRegions && ez <= MaxLookupRegions &&
                       ex * ey * ez <= MaxLookupRegions;

            sectorEnumerator = finished || byLookup ? default : sectors.GetEnumerator();
            lookupCursor = new int3(minSector.x - 1, minSector.y, minSector.z);
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

                if (!MoveToNextSector())
                {
                    finished = true;
                    return false;
                }
            }
        }

        /// <summary>Positions the brick enumerator on the next region that holds storage.</summary>
        private bool MoveToNextSector()
        {
            if (byLookup)
            {
                while (true)
                {
                    lookupCursor.x++;
                    if (lookupCursor.x > maxSector.x) { lookupCursor.x = minSector.x; lookupCursor.y++; }
                    if (lookupCursor.y > maxSector.y) { lookupCursor.y = minSector.y; lookupCursor.z++; }
                    if (lookupCursor.z > maxSector.z) { return false; }

                    if (!sectors.TryGetValue(lookupCursor, out SectorHandle found) || found.IsNull)
                    {
                        continue;
                    }

                    currentSectorPos = lookupCursor;
                    brickEnumerator = found.Get().EnumerateNonEmptyBricks();
                    inSector = true;
                    return true;
                }
            }

            while (sectorEnumerator.MoveNext())
            {
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
                return true;
            }

            return false;
        }
    }
}

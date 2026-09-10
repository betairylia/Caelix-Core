using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Walks the allocated bricks of one entity, yielding each brick's key together with its
    /// SOURCE dirty flags. Produced by <see cref="VoxelEntityData.EnumerateBrickSourceFlags"/>.
    /// </summary>
    /// <remarks>
    /// Burst-compatible and foreach-able: it implements no <c>IEnumerator</c> interface, so
    /// <c>foreach</c> binds to these members directly with no boxing. It allocates nothing and runs
    /// no job, which is what lets a consumer inside a parallel job use it. Order is storage order —
    /// region order, then brick index order inside a region — and is therefore not a spatial order.
    ///
    /// See <see cref="VoxelEntityData.EnumerateBrickSourceFlags"/> for who is allowed to read
    /// source flags at all.
    /// </remarks>
    public unsafe struct BrickSourceFlagsEnumerator
    {
        private UnsafeHashMap<int3, SectorHandle>.Enumerator sectorEnumerator;
        private SectorNonEmptyBrickEnumerator brickEnumerator;

        private readonly ushort mask;
        private readonly bool dirtyOnly;

        [NativeDisableUnsafePtrRestriction]
        private ushort* dirtyFlags;

        private int3 currentSectorPos;
        private BrickSourceFlags current;
        private bool inSector;
        private bool finished;

        /// <summary>Builds an enumerator over the allocated bricks of <paramref name="sectors"/>.</summary>
        internal BrickSourceFlagsEnumerator(
            SharedHashMap<int3, SectorHandle> sectors, DirtyFlags mask, bool dirtyOnly)
        {
            this.mask = (ushort)mask;
            this.dirtyOnly = dirtyOnly;

            finished = !sectors.IsCreated;
            sectorEnumerator = finished ? default : sectors.GetEnumerator();
            brickEnumerator = default;
            dirtyFlags = null;
            currentSectorPos = default;
            current = default;
            inSector = false;
        }

        /// <summary>Returns this enumerator (enables foreach usage).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BrickSourceFlagsEnumerator GetEnumerator() => this;

        /// <summary>The brick the enumerator currently points at, and its masked source flags.</summary>
        public BrickSourceFlags Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => current;
        }

        /// <summary>Advances to the next allocated brick the filter accepts.</summary>
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
                        int brickIdx = brickEnumerator.Current.BrickAbs;
                        var flags = (ushort)(dirtyFlags[brickIdx] & mask);
                        if (dirtyOnly && flags == 0)
                        {
                            continue;
                        }

                        current = new BrickSourceFlags
                        {
                            Key = SectorKeys.FromSector(currentSectorPos, brickIdx),
                            Flags = (DirtyFlags)flags
                        };
                        return true;
                    }

                    inSector = false;
                }

                if (!sectorEnumerator.MoveNext())
                {
                    finished = true;
                    return false;
                }

                SectorHandle handle = sectorEnumerator.Current.Value;
                if (handle.IsNull)
                {
                    continue;
                }

                ref Sector sector = ref handle.Get();
                if (dirtyOnly && (sector.sectorDirtyFlags & mask) == 0)
                {
                    continue;
                }

                currentSectorPos = sectorEnumerator.Current.Key;
                dirtyFlags = sector.brickDirtyFlags;
                brickEnumerator = sector.EnumerateNonEmptyBricks();
                inSector = true;
            }
        }
    }
}

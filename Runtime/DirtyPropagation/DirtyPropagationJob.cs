using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>What a brick needs doing. The same bit set names both flag sides.</summary>
    /// <remarks>
    /// A brick carries two of these: its DIRTY flags (<c>brickDirtyFlags</c>, what a write just
    /// changed inside it, cleared at the end of every tick) and its REQUIRE-UPDATE flags
    /// (<c>brickRequireUpdateFlags</c>, what work it needs after propagation, which survives to the
    /// next tick). The enum names neither side, which is why it is not called <c>DirtyFlags</c>;
    /// see <see cref="BrickChange"/> for the pair and who may read which.
    /// </remarks>
    /// <para>
    /// Bits 0–11 are the SIMULATION bits: application-defined automata channels the engine never
    /// interprets (an application names them, e.g. <c>Wire = Automata0</c>). Which of them a voxel
    /// write raises is decided by the writer, or by the application's
    /// <see cref="AutomataDefaults"/> when the writer passes no mask. Bits 12–15 are engine
    /// bookkeeping (allocation, geometry, replication) that every Block write raises regardless.
    /// </para>
    [Flags]
    public enum BrickUpdateFlags : ushort
    {
        None       = 0,
        Automata0  = 1 << 0,
        Automata1  = 1 << 1,
        Automata2  = 1 << 2,
        Automata3  = 1 << 3,
        Automata4  = 1 << 4,
        Automata5  = 1 << 5,
        Automata6  = 1 << 6,
        Automata7  = 1 << 7,
        Automata8  = 1 << 8,
        Automata9  = 1 << 9,
        Automata10 = 1 << 10,
        Automata11 = 1 << 11,
        BlockBrickAdded = 1 << 12,
        BlockBrickRemoved = 1 << 13,
        GeometryWithLocalNeighbor = 1 << 14,
        Geometry = 1 << 15,

        /// <summary>Every application simulation bit (bits 0–11).</summary>
        AutomataMask = 0x0FFF,
        All        = 0xFFFF,
    }

    public static class DirtyPropagationSettings
    {
        //                                                             15|                 |0
        public const ushort DirtyFlagsPropagateToAlien       = (ushort)0b0000_1111_1111_1111u;
        public const ushort DirtyFlagsPropagateToNeighbor    = (ushort)0b0100_1111_1111_1111u;
        public const ushort DirtyFlagsCanAllocateLocalBricks = (ushort)0b0000_1111_1111_1111u;

        /// <summary>
        /// Engine bookkeeping every changed Block write raises, whether or not the writer supplies
        /// simulation flags. No simulation bit is part of it: those come from the writer or from
        /// <see cref="AutomataDefaults"/>.
        /// </summary>
        public const BrickUpdateFlags BlockWriteGeometryFlags = (
            BrickUpdateFlags.GeometryWithLocalNeighbor |
            BrickUpdateFlags.Geometry);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort FilterCanPropagateToAlien(ushort flags)
        {
            return (ushort)(flags & DirtyFlagsPropagateToAlien);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort FilterCanPropagateToNeighbor(ushort flags)
        {
            return (ushort)(flags & DirtyFlagsPropagateToNeighbor);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort FilterCanAllocateLocalBricks(ushort flags)
        {
            return (ushort)(flags & DirtyFlagsCanAllocateLocalBricks);
        }
    }

    /// <summary>
    /// Burst-compiled parallel job that propagates dirty flags from bricks to their neighbors.
    /// </summary>
    /// <remarks>
    /// This job processes sectors in parallel and propagates dirty flags from each brick to its
    /// neighboring bricks, including those in adjacent sectors. This allows systems to efficiently
    /// determine which bricks need updating based on changes in neighboring bricks.
    /// The propagation respects the configured neighborhood type (Von Neumann or Moore).
    /// </remarks>
    [BurstCompile]
    internal unsafe struct DirtyPropagationJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int3> allSectorPositions;
        [ReadOnly] public SharedHashMap<int3, SectorHandle> sectors;
        [ReadOnly] public SharedHashMap<int3, SectorNeighborHandles> sectorNeighbors;
        [ReadOnly] public NeighborhoodType neighborhoodType;
        [ReadOnly] public BrickUpdateFlags flagsToPropagate;

        public void Execute(int index)
        {
            int3 sectorPos = allSectorPositions[index];
            if (!sectors.TryGetValue(sectorPos, out SectorHandle handle) || handle.IsNull) return;
            if (!sectorNeighbors.TryGetValue(sectorPos, out SectorNeighborHandles neighbors)) return;

            ref Sector sector = ref handle.Get();
            int neighborCount = NeighborhoodSettings.neighborhoodCount;
            ushort requestedFlags = (ushort)flagsToPropagate;
            ushort propagatingFlags = DirtyPropagationSettings.FilterCanPropagateToNeighbor(requestedFlags);

            // Create helper for convenient neighbor access
            var helper = new SectorNeighborhoodReaderHelper(handle, neighbors);

            // Early exit: Skip if current sector has no dirty flags AND no neighbors have dirty flags
            bool currentSectorDirty = (sector.sectorDirtyFlags & requestedFlags) != 0;

            if (!currentSectorDirty)
            {
                // Check if any neighbor sector has dirty flags
                bool anyNeighborDirty = false;
                for (int i = 0; i < neighborCount; i++)
                {
                    SectorHandle neighborHandle = neighbors.Neighbors[i];
                    if (!neighborHandle.IsNull && (neighborHandle.Get().sectorDirtyFlags & propagatingFlags) != 0)
                    {
                        anyNeighborDirty = true;
                        break;
                    }
                }

                // Skip processing if neither current sector nor any neighbor is dirty
                if (!anyNeighborDirty)
                {
                    return;
                }
            }

            // For each brick, check neighbors' dirty flags
            // Optimization: Only check neighbors that are actually relevant based on dirty direction masks
            for (int brickIdx = 0; brickIdx < Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS * Sector.SIZE_IN_BRICKS; brickIdx++)
            {
                int3 brickPos = Sector.ToBrickPos((short)brickIdx);
                ushort propagatedFlags = (ushort)(sector.brickDirtyFlags[brickIdx] & requestedFlags);

                // Check neighbors for dirty flags
                // Optimization: Only check neighbors that want to propagate in our direction
                for (int dir = 0; dir < neighborCount; dir++)
                {
                    int3 neighborBrickPos = brickPos + NeighborhoodSettings.Directions[dir];

                    // Get neighbor's direction mask to see if it wants to propagate to us
                    uint neighborDirMask = helper.GetBrickDirtyDirectionMask(neighborBrickPos);

                    // If neighbor has no direction mask, skip it (optimization)
                    if (!NeighborhoodSettings.HasAnyDirection(neighborDirMask)) continue;

                    // Calculate the direction from neighbor back to us
                    int oppositeDir = NeighborhoodSettings.OppositeDirectionIndices[dir];

                    // Skip if neighbor doesn't want to propagate in our direction
                    if (!NeighborhoodSettings.HasDirection(neighborDirMask, oppositeDir)) continue;

                    // Neighbor wants to propagate to us, so check its dirty flags
                    ushort neighborDirty = helper.GetBrickDirtyFlags(neighborBrickPos);
                    propagatedFlags |= (ushort)(neighborDirty & propagatingFlags);
                }

                if (propagatedFlags != 0)
                {
                    sector.brickRequireUpdateFlags[brickIdx] |= propagatedFlags;
                    sector.sectorRequireUpdateFlags |= propagatedFlags;
                }
            }
        }
    }
}

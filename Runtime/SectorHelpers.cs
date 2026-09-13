using System.Collections;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Walks a sector's bricks whose require-update flags match a mask, yielding each brick's
    /// compact brick id and its block-space origin. With <c>includeEmpty</c> it sweeps every brick
    /// position instead, so unallocated positions that a neighbour flagged are visited too.
    /// </summary>
    /// <remarks>
    /// Reads <c>brickRequireUpdateFlags</c>, the READ side of the dirty-flag contract: those flags
    /// are derived by dirty propagation from the raw dirty flags of a brick and its neighbours, and
    /// they persist past the end-of-tick <c>ClearDirtyFlags</c>. This is the enumerator ordinary
    /// consumers want. Its write-side twin is <see cref="SectorDirtyBrickEnumerator"/>; the two
    /// differ only in which flag array they read.
    ///
    /// Burst-compiled. The allocated-brick traversal is delegated to
    /// <see cref="SectorNonEmptyBrickEnumerator"/> whenever empty positions cannot match.
    ///
    /// Caller: <c>CollectBrickJob</c> in the Simulation assembly (Caelix), which gathers the bricks
    /// the automata stage must step.
    /// </remarks>
    [BurstCompile]
    internal struct SectorRequiredUpdateBrickEnumerator : IEnumerator<BrickIterator>
    {
        private Sector sector;
        private BrickUpdateFlags flagMask;
        private bool includeEmpty;

        private int bX, bY, bZ;
        private SectorNonEmptyBrickEnumerator nonEmptyBricks;

        // Compact (Relative) brick index
        private short sectorBrickIndex;

        /// <summary>
        /// Constructs a new sector enumerator for the specified sector.
        /// </summary>
        /// <param name="sector">The sector to enumerate.</param>
        public SectorRequiredUpdateBrickEnumerator(Sector sector, BrickUpdateFlags mask, bool includeEmpty)
        {
            this.sector = sector;

            bX = -1;
            bY = 0;
            bZ = 0;

            sectorBrickIndex = 0;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
            
            flagMask = mask;
            this.includeEmpty = includeEmpty;
        }

        /// <summary>
        /// Advances the enumerator to the next non-empty block.
        /// </summary>
        /// <returns>True if a non-empty block was found; false if enumeration is complete.</returns>
        public unsafe bool MoveNext()
        {
            // Skip clean sectors
            if((sector.sectorRequireUpdateFlags & (ushort)flagMask) == 0)
                return false;
            
            // Empty positions cannot match this branch, so delegate the allocated-brick traversal
            // to the shared enumerator and only filter its results by require-update flags here.
            if (!includeEmpty)
            {
                while (nonEmptyBricks.MoveNext())
                {
                    SectorNonEmptyBrickEnumerator.BrickRef brickRef = nonEmptyBricks.Current;
                    if ((sector.brickRequireUpdateFlags[brickRef.BrickAbs] & (ushort)flagMask) == 0)
                    {
                        continue;
                    }

                    sectorBrickIndex = brickRef.Bid;
                    int3 bPos = Sector.ToBrickPos((short)brickRef.BrickAbs);
                    bX = bPos.x;
                    bY = bPos.y;
                    bZ = bPos.z;
                    return true;
                }

                return false;
            }

            // We need to take care of empty spaces, use plain sweep instead.
            short absoluteBid;
            do
            {
                // Move to next brick
                bX++;
                if (bX >= Sector.SIZE_IN_BRICKS) { bX = 0; bY++; }
                if (bY >= Sector.SIZE_IN_BRICKS) { bY = 0; bZ++; }
                if (bZ >= Sector.SIZE_IN_BRICKS) { return false; }

                absoluteBid = (short)Sector.ToBrickIdx(bX, bY, bZ);
            } while ((sector.brickRequireUpdateFlags[absoluteBid] & (ushort)flagMask) == 0);

            return true;
        }

        /// <summary>
        /// Resets the enumerator to its initial state before the first element.
        /// </summary>
        public void Reset()
        {
            bX = -1;
            bY = 0;
            bZ = 0;
            sectorBrickIndex = 0;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
        }

        /// <summary>
        /// Gets the current brick and its position in the enumeration.
        /// </summary>
        public BrickIterator Current => new()
        {
            brickIdx = sectorBrickIndex,
            position = new int3(bX, bY, bZ) * Sector.SIZE_IN_BLOCKS,
        };

        /// <summary>
        /// Gets the current element (non-generic version).
        /// </summary>
        object IEnumerator.Current => Current;

        /// <summary>
        /// Disposes the enumerator. This enumerator has no unmanaged resources to release.
        /// </summary>
        public void Dispose()
        {
            // Nothing to dispose really
        }

        /// <summary>
        /// Returns this enumerator (enables foreach usage).
        /// </summary>
        /// <returns>This enumerator instance.</returns>
        public SectorRequiredUpdateBrickEnumerator GetEnumerator()
        {
            return this;
        }
    }
    
    /// <summary>
    /// One dirty brick of a sector: its ABSOLUTE index in the 4096-entry brick table (not the
    /// compact brick id) and the dirty flags of that brick that matched the enumerator's mask.
    /// </summary>
    internal struct DirtyBrickInfo
    {
        public short BrickIdx;
        public BrickUpdateFlags Flags;
    }

    /// <summary>
    /// Walks a sector's allocated bricks whose RAW dirty flags match a mask, yielding each brick's
    /// absolute index together with the matched flags.
    /// </summary>
    /// <remarks>
    /// Reads <c>brickDirtyFlags</c>, the WRITE side of the dirty-flag contract: those flags are set
    /// by voxel writers during a tick and cleared by <c>ClearDirtyFlags</c> at the end of it, so
    /// outside the tick they read as zero. Enumerating them is therefore only legitimate from inside
    /// the tick, after dirty propagation and before the clear. Ordinary consumers want the read-side
    /// twin <see cref="SectorRequiredUpdateBrickEnumerator"/> instead; the two differ only in which
    /// flag array they read.
    ///
    /// Burst-compiled, foreach-able, and built on <see cref="SectorNonEmptyBrickEnumerator"/>, so it
    /// never visits an unallocated brick position.
    ///
    /// Caller: server replication (phase A build job, Simulation assembly in Caelix), which needs
    /// exactly the bricks whose own contents changed this tick, not the propagated neighbourhood.
    /// </remarks>
    [BurstCompile]
    internal unsafe struct SectorDirtyBrickEnumerator
    {
        private Sector sector;
        private BrickUpdateFlags mask;
        private SectorNonEmptyBrickEnumerator nonEmptyBricks;
        private DirtyBrickInfo current;

        public SectorDirtyBrickEnumerator(Sector sector, BrickUpdateFlags mask)
        {
            this.sector = sector;
            this.mask = mask;
            nonEmptyBricks = sector.EnumerateNonEmptyBricks();
            current = default;
        }

        public SectorDirtyBrickEnumerator GetEnumerator() => this;
        public DirtyBrickInfo Current => current;

        public bool MoveNext()
        {
            if ((sector.sectorDirtyFlags & (ushort)mask) == 0)
            {
                return false;
            }

            while (nonEmptyBricks.MoveNext())
            {
                int brickIdx = nonEmptyBricks.Current.BrickAbs;
                ushort flags = (ushort)(sector.brickDirtyFlags[brickIdx] & (ushort)mask);
                if (flags != 0)
                {
                    current = new DirtyBrickInfo
                    {
                        BrickIdx = (short)brickIdx,
                        Flags = (BrickUpdateFlags)flags
                    };
                    return true;
                }
            }

            return false;
        }
    }
}

using System;
using Unity.Collections;
using Unity.Mathematics;
using Caelix.Net;

namespace Caelix
{
    /// <summary>
    /// The region half of the facade: residency of one entity's storage, keyed by region position
    /// in entity-local coordinates. See <see cref="VoxelRegion"/> for what a region is.
    /// </summary>
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>True when this entity holds storage for the region at <paramref name="regionPos"/>.</summary>
        public bool HasRegion(int3 regionPos) => sectors.IsCreated && sectors.ContainsKey(regionPos);

        /// <summary>Number of regions this entity holds. Zero on a <c>default</c> instance.</summary>
        public int RegionCount => sectors.IsCreated ? sectors.Count : 0;

        /// <summary>
        /// The positions of every region this entity holds. The caller owns the array. Returns an
        /// empty array on a <c>default</c> instance.
        /// </summary>
        public NativeArray<int3> GetRegionPositions(Allocator allocator)
            => sectors.IsCreated
                ? sectors.GetKeyArray(allocator)
                : new NativeArray<int3>(0, allocator);

        /// <summary>
        /// Makes the region at <paramref name="regionPos"/> exist, empty, when it does not yet.
        /// Allocates. No-op when the region is already there.
        /// </summary>
        public void EnsureRegion(int3 regionPos)
        {
            if (!sectors.IsCreated || sectors.ContainsKey(regionPos))
            {
                return;
            }

            AddEmptySectorAt(regionPos);
        }

        /// <summary>
        /// Takes ownership of a DETACHED region built by a generator and installs it at its own
        /// region position. Every allocated brick it brought is marked
        /// <see cref="DirtyFlags.BlockBrickAdded"/>, <see cref="DirtyFlags.Geometry"/> and
        /// <see cref="DirtyFlags.GeometryWithLocalNeighbor"/>, so the change list announces it; see
        /// the remarks on <see cref="AddSectorAt"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The region has no storage, is already attached, or this entity already holds a region at
        /// that position.
        /// </exception>
        public void AttachRegion(ref VoxelRegion region)
        {
            if (!region.IsCreated)
            {
                throw new InvalidOperationException("Cannot attach a region that has no storage.");
            }

            if (region.IsAttached)
            {
                throw new InvalidOperationException("This region is already attached to an entity.");
            }

            if (HasRegion(region.RegionPos))
            {
                throw new InvalidOperationException(
                    "This entity already holds a region at that position; remove it first.");
            }

            AddSectorAt(region.RegionPos, region.Handle);

            // AddSectorAt stamps an attachment identity onto its own copy of the handle. Republish
            // it so the caller's view is the one the entity holds.
            region.Handle = sectors[region.RegionPos];
            region.MarkAttached();
        }

        /// <summary>
        /// Frees the region at <paramref name="regionPos"/> and everything in it. Publishes one
        /// <see cref="ChangeKind.Removed"/> entry per allocated brick first and marks the facing
        /// bricks of the surviving neighbours; see <see cref="RemoveSectorAt"/>.
        /// </summary>
        /// <returns>False when this entity holds no region at that position.</returns>
        public bool RemoveRegion(int3 regionPos) => sectors.IsCreated && RemoveSectorAt(regionPos);

        /// <summary>
        /// Opens an ATTACHED view over the region at <paramref name="regionPos"/> for in-place
        /// reads and writes. Never allocates a region; the view's writes may allocate bricks.
        /// </summary>
        /// <returns>False (and <paramref name="region"/> <c>default</c>) when the region is absent.</returns>
        public bool TryOpenRegion(int3 regionPos, out VoxelRegion region)
        {
            if (!sectors.TryGetValue(regionPos, out SectorHandle handle) || handle.IsNull)
            {
                region = default;
                return false;
            }

            region = VoxelRegion.Attached(regionPos, handle);
            return true;
        }

        /// <summary>
        /// Allocated bricks across every region of this entity. Never allocates. Zero on a
        /// <c>default</c> instance.
        /// </summary>
        public int AllocatedBrickCount
        {
            get
            {
                if (!sectors.IsCreated)
                {
                    return 0;
                }

                int total = 0;
                foreach (var kvp in sectors)
                {
                    if (kvp.Value.IsNull)
                    {
                        continue;
                    }

                    total += kvp.Value.NonEmptyBrickCount;
                }

                return total;
            }
        }

        /// <summary>
        /// The entity-local block box that this entity's REGIONS cover, as a half-open range. It is
        /// residency, not content: a region contributes its whole box even when only one brick in it
        /// is allocated.
        /// </summary>
        /// <returns>False (and both outputs <c>default</c>) when the entity holds no region.</returns>
        public bool TryGetStorageBounds(out int3 minBlock, out int3 maxBlockExclusive)
        {
            minBlock = default;
            maxBlockExclusive = default;
            if (!sectors.IsCreated || sectors.Count == 0)
            {
                return false;
            }

            bool any = false;
            foreach (var kvp in sectors)
            {
                int3 origin = VoxelRegion.OriginOf(kvp.Key);
                int3 end = origin + VoxelRegion.SizeInBlocks;
                if (!any)
                {
                    minBlock = origin;
                    maxBlockExclusive = end;
                    any = true;
                    continue;
                }

                minBlock = math.min(minBlock, origin);
                maxBlockExclusive = math.max(maxBlockExclusive, end);
            }

            return any;
        }

        /// <summary>
        /// Writes one voxel's values for the slots in <paramref name="slotMask"/> that exist, in the
        /// wire form <c>u8 count</c> then <c>u8 slotId, u16 stride, byte[stride]</c> per record.
        /// Never allocates.
        /// </summary>
        /// <param name="blockPos">Entity-local block position.</param>
        /// <returns>
        /// False when no region holds <paramref name="blockPos"/>. NOTHING is written in that case,
        /// not even a zero count, so the caller decides what an absent voxel looks like on the wire.
        /// </returns>
        public bool WriteVoxelSlots(NetMessageWriter writer, int3 blockPos, ushort slotMask)
        {
            int3 regionPos = VoxelRegion.Of(blockPos);
            if (!sectors.TryGetValue(regionPos, out SectorHandle handle) || handle.IsNull)
            {
                return false;
            }

            int3 local = blockPos - VoxelRegion.OriginOf(regionPos);
            handle.Get().WriteVoxelSlots(writer, local.x, local.y, local.z, slotMask);
            return true;
        }
    }
}

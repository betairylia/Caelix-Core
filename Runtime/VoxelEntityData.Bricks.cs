using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>
    /// The brick-key facade: bulk access to one entity's storage keyed by
    /// <see cref="BrickKey">brick key</see> instead of by sector.
    /// </summary>
    /// <remarks>
    /// Every bound pointer follows the phase rule: mutate, allocate, apply or remove first, then
    /// bind, schedule and complete. A bind taken before a mutation is stale, and nothing
    /// re-validates it; <see cref="StorageEpoch"/> is what a consumer can assert against.
    /// </remarks>
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>True when the brick at <paramref name="key"/> is allocated.</summary>
        public bool IsBrickAllocated(int3 key)
        {
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return false;
            }

            return handle.Get().brickIdx[SectorKeys.ToBrickIdx(key)] != Sector.BRICKID_EMPTY;
        }

        /// <summary>
        /// Binds the per-voxel storage of one brick for one slot.
        /// </summary>
        /// <returns>
        /// False (and <paramref name="data"/> null) when the brick is not allocated or the slot has
        /// no storage. The pointer is the one the storage backend already holds; see the phase rule
        /// on this type.
        /// </returns>
        public bool TryBindBrick<T>(SectorSlotId slot, int3 key, out T* data)
            where T : unmanaged, IEquatable<T>
        {
            data = null;
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return false;
            }

            ref Sector sector = ref handle.Get();
            data = sector.GetBrick<T>(slot, sector.brickIdx[SectorKeys.ToBrickIdx(key)]);
            return data != null;
        }

        /// <summary>
        /// Binds the derived per-brick aux slice of one brick for one slot.
        /// </summary>
        /// <returns>
        /// False (and <paramref name="aux"/> null) when the brick is not allocated or the slot
        /// carries no aux buffer.
        /// </returns>
        public bool TryBindBrickAux(SectorSlotId slot, int3 key, out void* aux)
        {
            aux = null;
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return false;
            }

            ref Sector sector = ref handle.Get();
            aux = sector.GetBrickAuxPtr(slot, sector.brickIdx[SectorKeys.ToBrickIdx(key)]);
            return aux != null;
        }

        /// <summary>
        /// Allocated bricks whose key lies in <c>[minKey, maxKey]</c>, inclusive per axis. Never
        /// allocates. See <see cref="BrickKeyEnumerator"/> for the traversal order.
        /// </summary>
        public BrickKeyEnumerator EnumerateBricks(int3 minKey, int3 maxKey)
            => new BrickKeyEnumerator(sectors, minKey, maxKey);

        /// <summary>Every allocated brick of the entity. Never allocates.</summary>
        public BrickKeyEnumerator EnumerateBricks()
            => new BrickKeyEnumerator(sectors, new int3(int.MinValue), new int3(int.MaxValue));

        /// <summary>
        /// Opens the 27-brick read window around <paramref name="key"/>. Never allocates; see
        /// <see cref="VoxelNeighborhood"/> for its coordinate frame and valid range.
        /// </summary>
        public VoxelNeighborhood OpenNeighborhood(int3 key)
        {
            VoxelNeighborhood window = default;
            window.SetOrigin(BrickKey.ToBlockOrigin(key));

            // Neighbours mostly share one sector, so the last resolved sector is cached rather than
            // looked up 27 times.
            int3 cachedSectorPos = default;
            SectorHandle cachedHandle = default;
            bool hasCached = false;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int3 neighborKey = key + new int3(dx, dy, dz);
                int3 sectorPos = SectorKeys.ToSectorPos(neighborKey);

                if (!hasCached || math.any(sectorPos != cachedSectorPos))
                {
                    if (!sectors.TryGetValue(sectorPos, out cachedHandle))
                    {
                        cachedHandle = default;
                    }

                    cachedSectorPos = sectorPos;
                    hasCached = true;
                }

                Block* brick = null;
                if (!cachedHandle.IsNull)
                {
                    ref Sector sector = ref cachedHandle.Get();
                    brick = sector.GetBrick<Block>(
                        SectorSlotId.Block, sector.brickIdx[SectorKeys.ToBrickIdx(neighborKey)]);
                }

                window.SetBrick((dx + 1) + (dy + 1) * 3 + (dz + 1) * 9, brick);
            }

            return window;
        }

        /// <summary>
        /// Marks an EXISTING brick position as requiring work: the require-update side only, with
        /// no propagation and no allocation. No-op when the brick's storage does not exist.
        /// </summary>
        /// <remarks>For external markers such as alien propagation, which have already decided their targets.</remarks>
        public void MarkRequired(int3 key, DirtyFlags flags)
        {
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return;
            }

            handle.Get().MarkBrickRequireUpdate(SectorKeys.ToBrickIdx(key), flags);
        }

        /// <summary>
        /// Appends one <see cref="RequiredBrick"/> per brick whose require-update flags match
        /// <paramref name="mask"/>.
        /// </summary>
        /// <param name="entityId">Stamped on every record; the facade does not own the entity's identity.</param>
        /// <param name="mask">Require-update flags to select on.</param>
        /// <param name="includeEmpty">
        /// Visit unallocated brick positions too. That is the automata contract: a neighbour can
        /// flag a position that holds no storage yet, and the stage is expected to see it.
        /// </param>
        /// <param name="into">Destination list. Entries are appended, never cleared.</param>
        public void CollectRequiredBricks(
            Guid128 entityId, DirtyFlags mask, bool includeEmpty, NativeList<RequiredBrick> into)
        {
            if (!sectors.IsCreated || !into.IsCreated)
            {
                return;
            }

            new CollectRequiredBricksJob
            {
                data = this,
                entityId = entityId,
                mask = mask,
                includeEmpty = includeEmpty,
                into = into
            }.Run();
        }
    }

    /// <summary>
    /// Collects one entity's require-update bricks as keyed records. A Burst job rather than a
    /// Burst direct call, for the reason given on <see cref="BuildChangeListJob"/>.
    /// </summary>
    [BurstCompile]
    internal unsafe struct CollectRequiredBricksJob : IJob
    {
        /// <summary>The entity to scan.</summary>
        [ReadOnly] public VoxelEntityData data;

        /// <summary>Identity stamped on every produced record.</summary>
        public Guid128 entityId;

        /// <summary>Require-update flags to select on.</summary>
        public DirtyFlags mask;

        /// <summary>Visit unallocated brick positions too.</summary>
        public bool includeEmpty;

        /// <summary>Destination list.</summary>
        public NativeList<RequiredBrick> into;

        public void Execute()
        {
            foreach (var sectorEntry in data.sectors)
            {
                ref Sector sector = ref sectorEntry.Value.Get();
                var enumerator = new SectorRequiredUpdateBrickEnumerator(sector, mask, includeEmpty);
                foreach (BrickIterator brick in enumerator)
                {
                    int3 brickPos = brick.position >> BrickKey.Shift;
                    int brickIdxAbs = Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z);

                    into.Add(new RequiredBrick
                    {
                        Entity = entityId,
                        Key = SectorKeys.FromSector(sectorEntry.Key, brickIdxAbs),
                        Flags = (DirtyFlags)sector.brickRequireUpdateFlags[brickIdxAbs],
                        LocalToWorld = data.transform
                    });
                }
            }
        }
    }
}

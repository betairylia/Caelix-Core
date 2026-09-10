using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>One brick's key together with its SOURCE dirty flags.</summary>
    /// <remarks>
    /// Produced by <see cref="VoxelEntityData.EnumerateBrickSourceFlags"/>. <c>Flags</c> is the
    /// brick's own dirty flags masked by the enumeration's mask, so it can be zero when the
    /// enumeration was not restricted to dirty bricks.
    /// </remarks>
    public struct BrickSourceFlags
    {
        /// <summary>Entity-local brick key. See <see cref="BrickKey"/>.</summary>
        public int3 Key;

        /// <summary>The brick's own dirty flags, masked.</summary>
        public DirtyFlags Flags;
    }

    /// <summary>
    /// The brick-key facade: bulk access to one entity's storage keyed by
    /// <see cref="BrickKey">brick key</see> instead of by sector.
    /// </summary>
    /// <remarks>
    /// Every bound pointer follows the phase rule: mutate, allocate, apply or remove first, then
    /// bind, schedule and complete. A bind taken before a mutation is stale, and nothing
    /// re-validates it; <see cref="StorageEpoch"/> is what a consumer can assert against.
    ///
    /// Every member here tolerates a <c>default</c> instance, which holds no storage at all: it
    /// reads as an entity with no regions and no bricks, and its markers are no-ops.
    /// </remarks>
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>True when the brick at <paramref name="key"/> is allocated.</summary>
        public bool IsBrickAllocated(int3 key)
        {
            BrickCursor cursor = default;
            return IsBrickAllocated(key, ref cursor);
        }

        /// <summary>
        /// True when the brick at <paramref name="key"/> is allocated, reusing
        /// <paramref name="cursor"/>'s last region lookup. See <see cref="BrickCursor"/>.
        /// </summary>
        public bool IsBrickAllocated(int3 key, ref BrickCursor cursor)
        {
            if (!TryResolveRegion(key, ref cursor, out SectorHandle handle))
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
            BrickCursor cursor = default;
            return TryBindBrick(slot, key, ref cursor, out data);
        }

        /// <summary>
        /// Binds the per-voxel storage of one brick for one slot, reusing <paramref name="cursor"/>'s
        /// last region lookup. See <see cref="BrickCursor"/>.
        /// </summary>
        public bool TryBindBrick<T>(SectorSlotId slot, int3 key, ref BrickCursor cursor, out T* data)
            where T : unmanaged, IEquatable<T>
        {
            data = null;
            if (!TryResolveRegion(key, ref cursor, out SectorHandle handle))
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
            BrickCursor cursor = default;
            return TryBindBrickAux(slot, key, ref cursor, out aux);
        }

        /// <summary>
        /// Binds the derived per-brick aux slice of one brick for one slot, reusing
        /// <paramref name="cursor"/>'s last region lookup. See <see cref="BrickCursor"/>.
        /// </summary>
        public bool TryBindBrickAux(SectorSlotId slot, int3 key, ref BrickCursor cursor, out void* aux)
        {
            aux = null;
            if (!TryResolveRegion(key, ref cursor, out SectorHandle handle))
            {
                return false;
            }

            ref Sector sector = ref handle.Get();
            aux = sector.GetBrickAuxPtr(slot, sector.brickIdx[SectorKeys.ToBrickIdx(key)]);
            return aux != null;
        }

        /// <summary>
        /// Resolves the region that holds <paramref name="key"/>, through the cursor's cache. An
        /// absent region is cached as a null handle, so repeated misses cost one lookup per region.
        /// </summary>
        private bool TryResolveRegion(int3 key, ref BrickCursor cursor, out SectorHandle handle)
        {
            int3 sectorPos = SectorKeys.ToSectorPos(key);
            if (!cursor.Valid || math.any(cursor.SectorPos != sectorPos))
            {
                if (!sectors.TryGetValue(sectorPos, out cursor.Handle))
                {
                    cursor.Handle = default;
                }

                cursor.SectorPos = sectorPos;
                cursor.Valid = true;
            }

            handle = cursor.Handle;
            return !handle.IsNull;
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
        /// Allocated bricks with their SOURCE dirty flags. Never allocates and runs no job, so a
        /// consumer inside a parallel job can use it.
        /// </summary>
        /// <param name="mask">Flags to keep; the yielded <c>Flags</c> are masked by it.</param>
        /// <param name="dirtyOnly">
        /// True yields only the bricks whose masked source flags are non-zero (whole clean regions
        /// are skipped); false yields every allocated brick, with flags that may be zero.
        /// </param>
        /// <remarks>
        /// This is the bridge-phase reader of SOURCE dirty flags — only alien propagation and
        /// replication may read those, and only from inside the tick, after propagation and before
        /// the end-of-tick clear. Everything else reads <see cref="GetRequiredFlags"/>,
        /// <see cref="CollectRequiredBricks"/> or <see cref="Changes"/>.
        /// </remarks>
        public BrickSourceFlagsEnumerator EnumerateBrickSourceFlags(DirtyFlags mask, bool dirtyOnly)
            => new BrickSourceFlagsEnumerator(sectors, mask, dirtyOnly);

        /// <summary>
        /// Enumerates the values of one brick's slot selected by that slot's per-brick bitmap aux
        /// buffer, at entity-local block positions. Never allocates.
        /// </summary>
        /// <remarks>
        /// Yields nothing when the brick, the slot data or the aux bitmap is missing, so a caller
        /// does not have to test residency first. The bitmap must be current: it is rebuilt from
        /// settled voxel data by the owning refresh pass (Block occupancy by
        /// <see cref="RefreshNonEmptyMask"/>).
        /// </remarks>
        public BrickBitmaskSlotEnumerator<T> EnumerateBrickBitmask<T>(SectorSlotId slot, int3 key)
            where T : unmanaged
        {
            int3 origin = BrickKey.ToBlockOrigin(key);
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return new BrickBitmaskSlotEnumerator<T>(null, null, origin);
            }

            ref Sector sector = ref handle.Get();
            short bid = sector.brickIdx[SectorKeys.ToBrickIdx(key)];
            if (bid == Sector.BRICKID_EMPTY)
            {
                return new BrickBitmaskSlotEnumerator<T>(null, null, origin);
            }

            SectorSlotStorage* storage = sector.slots + (int)slot;
            T* data = storage->IsCreated ? (T*)storage->GetBrickPtr(bid) : null;
            var aux = (ulong*)sector.GetBrickAuxPtr(slot, bid);
            return new BrickBitmaskSlotEnumerator<T>(data, aux, origin);
        }

        /// <summary>
        /// Opens the 27-brick read window around <paramref name="key"/>. Never allocates; see
        /// <see cref="VoxelNeighborhood"/> for its coordinate frame and valid range.
        /// </summary>
        public VoxelNeighborhood OpenNeighborhood(int3 key)
            => OpenNeighborhood(key, SectorSlotId.Block, false);

        /// <summary>
        /// Opens the 27-brick read window around <paramref name="key"/> and also binds each
        /// neighbour's per-brick aux slice for <paramref name="auxSlot"/>, readable through
        /// <see cref="VoxelNeighborhood.GetAuxPtr"/>. Never allocates.
        /// </summary>
        /// <remarks>
        /// Bulk producers that walk a voxel neighbourhood read whole per-brick bitmasks instead of
        /// testing voxels one at a time; this binds the 27 of them in the same pass that binds the
        /// block storage. A neighbour with no brick, or a slot with no aux, binds as null.
        /// </remarks>
        public VoxelNeighborhood OpenNeighborhood(int3 key, SectorSlotId auxSlot)
            => OpenNeighborhood(key, auxSlot, true);

        private VoxelNeighborhood OpenNeighborhood(int3 key, SectorSlotId auxSlot, bool bindAux)
        {
            VoxelNeighborhood window = default;
            window.SetOrigin(BrickKey.ToBlockOrigin(key));

            // Neighbours mostly share one sector, so the last resolved sector is cached rather than
            // looked up 27 times.
            BrickCursor cursor = default;

            for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int3 neighborKey = key + new int3(dx, dy, dz);
                int index = VoxelNeighborhood.Index(dx, dy, dz);

                if (!TryResolveRegion(neighborKey, ref cursor, out SectorHandle handle))
                {
                    window.SetBrick(index, null);
                    continue;
                }

                ref Sector sector = ref handle.Get();
                short bid = sector.brickIdx[SectorKeys.ToBrickIdx(neighborKey)];
                window.SetBrick(index, sector.GetBrick<Block>(SectorSlotId.Block, bid));
                if (bindAux)
                {
                    window.SetAux(index, sector.GetBrickAuxPtr(auxSlot, bid));
                }
            }

            return window;
        }

        /// <summary>
        /// The require-update flags of the brick position at <paramref name="key"/>: the work that
        /// brick needs after propagation. Zero when no region holds the key. Never allocates.
        /// </summary>
        /// <remarks>
        /// This is the READ side of the dirty contract and what an ordinary consumer wants. It is
        /// defined for unallocated brick positions too, because a neighbour can flag a position
        /// that holds no storage yet.
        /// </remarks>
        public DirtyFlags GetRequiredFlags(int3 key)
        {
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return DirtyFlags.None;
            }

            return (DirtyFlags)handle.Get().brickRequireUpdateFlags[SectorKeys.ToBrickIdx(key)];
        }

        /// <summary>
        /// Marks a brick position as dirty: the SOURCE side, which the next propagation turns into
        /// the require-update flags of this brick and its neighbours. Never allocates; no-op when no
        /// region holds the key.
        /// </summary>
        /// <param name="key">Entity-local brick key.</param>
        /// <param name="flags">Dirty flags to set.</param>
        /// <param name="directionMask">
        /// Which of the 26 neighbour directions the flags may propagate to; <c>0xFFFFFFFF</c> is all
        /// of them, <c>0</c> keeps them local to this brick.
        /// </param>
        /// <remarks>
        /// Marks BY POSITION, not by allocated brick: propagation reads the flag arrays by index and
        /// never asks whether a brick still exists, so a position whose storage was just freed can
        /// still tell its neighbours to rebuild.
        /// </remarks>
        public void MarkBrickDirty(int3 key, DirtyFlags flags, uint directionMask = 0xFFFFFFFF)
        {
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return;
            }

            handle.Get().MarkBrickDirty(SectorKeys.ToBrickIdx(key), flags, directionMask);
        }

        /// <summary>
        /// Marks an EXISTING brick position as requiring work: the require-update side only, with
        /// no propagation and no allocation. No-op when the region or the brick is not allocated.
        /// </summary>
        /// <returns>True when the brick existed and was marked.</returns>
        /// <remarks>For external markers such as alien propagation, which have already decided their targets.</remarks>
        public bool MarkRequired(int3 key, DirtyFlags flags)
        {
            if (!sectors.TryGetValue(SectorKeys.ToSectorPos(key), out SectorHandle handle) || handle.IsNull)
            {
                return false;
            }

            ref Sector sector = ref handle.Get();
            int brickIdx = SectorKeys.ToBrickIdx(key);
            if (sector.brickIdx[brickIdx] == Sector.BRICKID_EMPTY)
            {
                // An unallocated position has nothing to work on; marking it would surface as
                // phantom automata work through CollectRequiredBricks(includeEmpty: true).
                return false;
            }

            sector.MarkBrickRequireUpdate(brickIdx, flags);
            return true;
        }

        /// <summary>
        /// Makes a DERIVED slot exist in every region of this entity, with a per-voxel stride of
        /// <c>sizeof(T)</c> and a per-brick aux buffer of <paramref name="auxBytesPerBrick"/> bytes.
        /// Allocates. Safe to call repeatedly.
        /// </summary>
        /// <param name="newlyCoveredBricks">
        /// Receives the key of every allocated brick of a region whose slot was created, replaced or
        /// given a fresh aux buffer: those bricks hold no valid derived data yet and the producer
        /// must fill them. Entries are appended, never cleared. May be a default (uncreated) list
        /// when the caller does not want them.
        /// </param>
        /// <exception cref="ArgumentException">
        /// <paramref name="slot"/> is <see cref="SectorSlotId.Block"/>. Block is the authoritative
        /// voxel data, not a derived slot, and must never be recreated under a consumer's feet.
        /// </exception>
        /// <remarks>
        /// A region whose slot exists with a different stride has that slot DISPOSED and recreated:
        /// a derived slot is reproducible from Block data, so a stale layout (a hot reload, a save
        /// written by an older build) is replaced rather than reinterpreted.
        /// </remarks>
        public void EnsureSlot<T>(SectorSlotId slot, int auxBytesPerBrick, NativeList<int3> newlyCoveredBricks)
            where T : unmanaged
        {
            if (slot == SectorSlotId.Block)
            {
                throw new ArgumentException(
                    "EnsureSlot cannot recreate the Block slot; it holds the authoritative voxel data.",
                    nameof(slot));
            }

            if (!sectors.IsCreated || sectors.Count == 0)
            {
                return;
            }

            int stride = UnsafeUtility.SizeOf<T>();
            var rebuilt = new NativeList<SectorHandle>(sectors.Count, Allocator.TempJob);
            var rebuiltPositions = new NativeList<int3>(sectors.Count, Allocator.TempJob);
            try
            {
                foreach (var kvp in sectors)
                {
                    if (kvp.Value.IsNull)
                    {
                        continue;
                    }

                    ref Sector sector = ref kvp.Value.Get();
                    SectorSlotStorage* storage = sector.slots + (int)slot;
                    bool rebuild = !storage->IsCreated ||
                                   storage->stride != stride ||
                                   (auxBytesPerBrick > 0 && !storage->HasAux);
                    if (!rebuild)
                    {
                        continue;
                    }

                    if (storage->IsCreated && storage->stride != stride)
                    {
                        storage->Dispose();
                        *storage = default;
                    }

                    // Allocation stays on the main thread, mirroring every other slot producer, so
                    // the jobs that fill the slot only ever write into existing memory.
                    sector.EnsureSlotAllocated<T>(slot, auxBytesPerBrick);

                    rebuilt.Add(kvp.Value);
                    rebuiltPositions.Add(kvp.Key);
                }

                if (rebuilt.Length != 0 && newlyCoveredBricks.IsCreated)
                {
                    new CollectRegionBrickKeysJob
                    {
                        handles = rebuilt.AsArray(),
                        positions = rebuiltPositions.AsArray(),
                        into = newlyCoveredBricks
                    }.Run();
                }
            }
            finally
            {
                rebuilt.Dispose();
                rebuiltPositions.Dispose();
            }
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

    /// <summary>
    /// Appends the keys of every allocated brick of a set of regions. A Burst job rather than a
    /// Burst direct call, for the reason given on <see cref="BuildChangeListJob"/>.
    /// </summary>
    [BurstCompile]
    internal unsafe struct CollectRegionBrickKeysJob : IJob
    {
        /// <summary>Storage of the regions to scan.</summary>
        [ReadOnly] public NativeArray<SectorHandle> handles;

        /// <summary>Position of each region, parallel to <see cref="handles"/>.</summary>
        [ReadOnly] public NativeArray<int3> positions;

        /// <summary>Destination list.</summary>
        public NativeList<int3> into;

        public void Execute()
        {
            for (int i = 0; i < handles.Length; i++)
            {
                SectorHandle handle = handles[i];
                ref Sector sector = ref handle.Get();
                foreach (SectorNonEmptyBrickEnumerator.BrickRef brick in sector.EnumerateNonEmptyBricks())
                {
                    into.Add(SectorKeys.FromSector(positions[i], brick.BrickAbs));
                }
            }
        }
    }
}

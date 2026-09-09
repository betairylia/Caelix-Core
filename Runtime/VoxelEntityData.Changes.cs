using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// The per-cycle change list of one entity, plus the storage epoch. Heap-allocated so that
    /// every copy of a <see cref="VoxelEntityData"/> shares one list — the same rule
    /// <see cref="SharedHashMap{TKey,TValue}"/> follows, and for the same reason.
    /// </summary>
    internal unsafe struct EntityChangeLog
    {
        /// <summary>The entries published this cycle. Removals are appended as they happen.</summary>
        public UnsafeList<BrickChange> Entries;

        /// <summary>Advances whenever storage may have moved. See <see cref="VoxelEntityData.StorageEpoch"/>.</summary>
        public uint Epoch;

        /// <summary>Debug guard only: set by BuildChangeList, cleared by ClearChanges.</summary>
        public bool BuiltThisCycle;
    }

    public unsafe partial struct VoxelEntityData
    {
        /// <summary>
        /// The shared change log. Allocated in the constructor, freed in <see cref="Dispose"/>.
        /// Null on a <c>default</c> instance, which every member below tolerates.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        private EntityChangeLog* changeLog;

        /// <summary>Allocates the shared change log. Called once, from the constructor.</summary>
        private void InitializeChangeLog(Allocator allocator)
        {
            changeLog = (EntityChangeLog*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<EntityChangeLog>(),
                UnsafeUtility.AlignOf<EntityChangeLog>(),
                Allocator.Persistent);

            *changeLog = new EntityChangeLog
            {
                Entries = new UnsafeList<BrickChange>(64, allocator),
                Epoch = 0,
                BuiltThisCycle = false
            };
        }

        /// <summary>Releases the shared change log. Called from <see cref="Dispose"/>.</summary>
        private void DisposeChangeLog()
        {
            if (changeLog == null)
            {
                return;
            }

            if (changeLog->Entries.IsCreated)
            {
                changeLog->Entries.Dispose();
            }

            UnsafeUtility.Free(changeLog, Allocator.Persistent);
            changeLog = null;
        }

        /// <summary>Number of change entries in the current cycle.</summary>
        public int ChangeCount => changeLog != null ? changeLog->Entries.Length : 0;

        /// <summary>
        /// Read-only view of the change list. Valid until the next <see cref="BuildChangeList"/>,
        /// <see cref="ClearChanges"/>, <see cref="RemoveSectorAt"/> or <see cref="Dispose"/>.
        /// </summary>
        /// <remarks>
        /// An alias, not a copy: the store owns the memory and nothing outside Caelix-Core appends
        /// to it or clears it. An empty list returns a default view, whose <c>Length</c> is 0.
        /// </remarks>
        public NativeArray<BrickChange>.ReadOnly Changes
        {
            get
            {
                if (changeLog == null || changeLog->Entries.Length == 0)
                {
                    return default;
                }

                NativeArray<BrickChange> array =
                    NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<BrickChange>(
                        changeLog->Entries.Ptr, changeLog->Entries.Length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                NativeArrayUnsafeUtility.SetAtomicSafetyHandle(
                    ref array, AtomicSafetyHandle.GetTempUnsafePtrSliceHandle());
#endif
                return array.AsReadOnly();
            }
        }

        /// <summary>One change entry by index, or <c>default</c> when there is no change log.</summary>
        public BrickChange GetChange(int index)
        {
            if (changeLog == null)
            {
                return default;
            }

            CheckChangeIndex(index);
            return changeLog->Entries[index];
        }

        /// <summary>
        /// Appends one <see cref="ChangeKind.Updated"/> entry per allocated brick whose
        /// (dirty | require-update) flags are non-zero.
        /// </summary>
        /// <remarks>
        /// Call exactly once per cycle, after propagation is final: on the server after alien
        /// propagation, on the client after <c>PropagateForRender</c>. <see cref="ChangeKind.Removed"/>
        /// entries appended earlier in the cycle by <see cref="RemoveSectorAt"/> stay in place,
        /// before the updated entries. Unallocated brick positions are never listed: they hold no
        /// storage to read, so a consumer has nothing to do for them.
        /// </remarks>
        public void BuildChangeList()
        {
            if (changeLog == null || !sectors.IsCreated)
            {
                return;
            }

            CheckNotBuiltThisCycle();
            changeLog->BuiltThisCycle = true;
            new BuildChangeListJob { data = this }.Run();
        }

        /// <summary>
        /// Empties the change list. The owner calls it at the end of its cycle: the server world at
        /// end of tick, the client world at end of frame.
        /// </summary>
        public void ClearChanges()
        {
            if (changeLog == null)
            {
                return;
            }

            // A join lists every brick of the world once; the list must not keep that peak
            // capacity for the rest of the session, so an oversized buffer is released here and
            // regrows by doubling on the next large cycle.
            if (changeLog->Entries.Capacity > TrimCapacity)
            {
                AllocatorManager.AllocatorHandle allocator = changeLog->Entries.Allocator;
                changeLog->Entries.Dispose();
                changeLog->Entries = new UnsafeList<BrickChange>(64, allocator);
            }
            else
            {
                changeLog->Entries.Clear();
            }

            changeLog->BuiltThisCycle = false;
        }

        /// <summary>Entry count above which <see cref="ClearChanges"/> releases the buffer instead of keeping it.</summary>
        private const int TrimCapacity = 1 << 16;

        /// <summary>
        /// Counter that advances whenever storage may have moved: <see cref="AddSectorAt"/>,
        /// <see cref="RemoveSectorAt"/>, <see cref="MarkStorageMutated"/>.
        /// </summary>
        /// <remarks>
        /// A bound brick pointer is valid only while this value is unchanged. Nothing re-validates
        /// it in this milestone; it exists so a consumer can assert the phase rule.
        /// </remarks>
        public uint StorageEpoch => changeLog != null ? changeLog->Epoch : 0u;

        /// <summary>
        /// Declares that storage may have moved, so bound pointers taken before this call are
        /// stale. The world calls it after the automata snapshot swap.
        /// </summary>
        public void MarkStorageMutated()
        {
            if (changeLog == null)
            {
                return;
            }

            changeLog->Epoch++;
        }

        /// <summary>
        /// Appends one <see cref="ChangeKind.Removed"/> entry per allocated brick of the sector at
        /// <paramref name="sectorPos"/>. Must run before the storage is freed: a scan of what is
        /// left cannot discover a removal.
        /// </summary>
        private void AppendRemovedBricks(int3 sectorPos)
        {
            if (changeLog == null)
            {
                return;
            }

            if (!sectors.TryGetValue(sectorPos, out SectorHandle handle) || handle.IsNull)
            {
                return;
            }

            ref Sector sector = ref handle.Get();
            foreach (SectorNonEmptyBrickEnumerator.BrickRef brick in sector.EnumerateNonEmptyBricks())
            {
                changeLog->Entries.Add(new BrickChange
                {
                    Key = SectorKeys.FromSector(sectorPos, brick.BrickAbs),
                    Kind = ChangeKind.Removed,
                    SourceFlags = DirtyFlags.BlockBrickRemoved,
                    RequiredFlags = DirtyFlags.BlockBrickRemoved
                });
            }
        }

        /// <summary>
        /// Body of <see cref="BuildChangeList"/>, run inside a Burst job. Mutates only through
        /// <see cref="changeLog"/>, which every copy of this struct shares.
        /// </summary>
        internal void AppendUpdatedBricks()
        {
            EntityChangeLog* log = changeLog;
            foreach (var sectorEntry in sectors)
            {
                ref Sector sector = ref sectorEntry.Value.Get();
                if ((sector.sectorDirtyFlags | sector.sectorRequireUpdateFlags) == 0)
                {
                    continue;
                }

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brick in sector.EnumerateNonEmptyBricks())
                {
                    ushort dirty = sector.brickDirtyFlags[brick.BrickAbs];
                    ushort required = sector.brickRequireUpdateFlags[brick.BrickAbs];
                    if ((dirty | required) == 0)
                    {
                        continue;
                    }

                    log->Entries.Add(new BrickChange
                    {
                        Key = SectorKeys.FromSector(sectorEntry.Key, brick.BrickAbs),
                        Kind = ChangeKind.Updated,
                        SourceFlags = (DirtyFlags)dirty,
                        RequiredFlags = (DirtyFlags)required
                    });
                }
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckNotBuiltThisCycle()
        {
            if (changeLog->BuiltThisCycle)
            {
                throw new InvalidOperationException(
                    "BuildChangeList was already called this cycle; call ClearChanges first.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckChangeIndex(int index)
        {
            if (index < 0 || index >= changeLog->Entries.Length)
            {
                throw new IndexOutOfRangeException("Change index is outside the current change list.");
            }
        }
    }

    /// <summary>
    /// Fills one entity's change list from its brick flag arrays. A Burst job rather than a Burst
    /// direct call: a direct call compiles synchronously on the main thread the first time it runs,
    /// which stalls the editor with no log line.
    /// </summary>
    [BurstCompile]
    internal unsafe struct BuildChangeListJob : IJob
    {
        /// <summary>The entity to scan. A copy; the appends go through its shared change log.</summary>
        [ReadOnly] public VoxelEntityData data;

        public void Execute()
        {
            data.AppendUpdatedBricks();
        }
    }
}

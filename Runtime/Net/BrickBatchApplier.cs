using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Caelix.Net
{
    /// <summary>
    /// The only apply path for <see cref="NetMessageType.BrickBatch"/> payloads. Queue the messages
    /// of one receive pass with <see cref="Add"/>, then <see cref="Flush"/> once: the applier
    /// validates every message, groups the records by the storage unit that owns them, applies
    /// them, publishes the removals and frees a unit that ended up empty. Nothing outside
    /// Caelix-Core learns which sector a key falls into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only for <see cref="Add"/>, <see cref="Flush"/> and <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// Ordering. Messages of one entity apply in the order they were added; different entities and
    /// different sectors are independent and apply in parallel. A key may legally appear as Remove
    /// and then Update in one flush — that is what a sector replaced in a single server tick looks
    /// like — and it ends allocated, with both a <see cref="ChangeKind.Removed"/> entry from here
    /// and an <see cref="ChangeKind.Updated"/> entry that <c>BuildChangeList</c> appends later in
    /// the same client frame. Removed entries therefore always precede the cycle's updates, which
    /// is the order the change-list contract promises.
    /// </para>
    /// <para>
    /// Removals keep the dirty flags of a freed brick index
    /// (<see cref="BrickReplication.ReplicationRemoveFlags"/>): propagation reads the flag arrays
    /// by index and never asks whether the brick still exists, so its 26 neighbours are re-rendered
    /// even though the storage is gone.
    /// </para>
    /// <para>
    /// An emptied storage unit is freed at once, which mirrors the server: its only per-brick
    /// removal path is a whole-sector removal, so a replica that kept empty units would not match.
    /// The boundary marking that freeing does covers the cross-unit neighbours the per-brick flags
    /// can no longer reach.
    /// </para>
    /// <para>
    /// An invalid message changes no brick and creates no storage; earlier and later messages in
    /// the same flush still apply, and <see cref="Errors"/> names the failure per message.
    /// </para>
    /// </remarks>
    public sealed unsafe class BrickBatchApplier : IDisposable
    {
        /// <summary>One entity whose storage this flush writes into.</summary>
        internal struct EntityRef
        {
            public VoxelEntityData Data;
        }

        /// <summary>One queued message: the caller-owned payload after the batch header.</summary>
        internal struct Message
        {
            public int Entity;
            [NativeDisableUnsafePtrRestriction] public byte* Payload;
            public int Length;
            public int BrickCount;
        }

        /// <summary>One validated record, chained into its sector's work item.</summary>
        internal struct Record
        {
            public int Message;

            /// <summary>Offset of the byte AFTER the record header, inside the message payload.</summary>
            public int Offset;
            public int3 Key;
            public BrickOp Op;
            public int Next;
        }

        /// <summary>All records of one flush that land in one sector of one entity.</summary>
        internal struct SectorWork
        {
            public int Entity;
            public int3 SectorPos;
            public SectorHandle Sector;
            public int First;
            public int Last;
        }

        internal struct WorkKey : IEquatable<WorkKey>
        {
            public int Entity;
            public int3 SectorPos;

            public bool Equals(WorkKey other)
                => Entity == other.Entity && math.all(SectorPos == other.SectorPos);

            public override int GetHashCode()
                => (int)math.hash(new int4(SectorPos, Entity));
        }

        internal struct StrideKey : IEquatable<StrideKey>
        {
            public int Entity;
            public int3 SectorPos;
            public int SlotId;

            public bool Equals(StrideKey other)
                => Entity == other.Entity && SlotId == other.SlotId && math.all(SectorPos == other.SectorPos);

            public override int GetHashCode()
                => (int)math.hash(new int4(SectorPos, Entity)) ^ (SlotId * 0x27D4EB2D);
        }

        /// <summary>One brick the apply pass freed, to be published on the main thread.</summary>
        internal struct RemovedBrick
        {
            public int Entity;
            public int3 Key;
        }

        private NativeList<EntityRef> entities = new(8, Allocator.Persistent);
        private NativeList<Message> messages = new(64, Allocator.Persistent);
        private NativeList<Record> records = new(256, Allocator.Persistent);
        private NativeList<SectorWork> work = new(64, Allocator.Persistent);
        private NativeList<StrideKey> addedThisMessage = new(Sector.MAX_SLOTS, Allocator.Persistent);
        private NativeList<RemovedBrick> removed = new(64, Allocator.Persistent);
        private NativeList<ReplicatedBrickBatchError> errors = new(64, Allocator.Persistent);
        private NativeHashMap<WorkKey, int> workIndex = new(64, Allocator.Persistent);
        private NativeHashMap<StrideKey, int> strides = new(64, Allocator.Persistent);
        private NativeArray<int> removeCapacity = new(1, Allocator.Persistent);

        /// <summary>Index of the entity the last <see cref="Add"/> resolved, or -1. See <see cref="ResolveEntity"/>.</summary>
        private int lastEntity = -1;
        private bool disposed;

        /// <summary>Number of messages queued since the last <see cref="Flush"/>.</summary>
        public int MessageCount => messages.Length;

        /// <summary>
        /// Per-message result of the last <see cref="Flush"/>, indexed like the <see cref="Add"/>
        /// order. Valid until the next <see cref="Add"/>.
        /// </summary>
        public NativeArray<ReplicatedBrickBatchError>.ReadOnly Errors => errors.AsArray().AsReadOnly();

        /// <summary>
        /// Queues one message. <paramref name="payload"/> is the bytes after the
        /// <see cref="BrickBatchHeader"/> and must stay valid and unchanged until
        /// <see cref="Flush"/> returns. Messages of one entity apply in the order they were added.
        /// </summary>
        /// <param name="data">The entity the batch header named. Copies share one store.</param>
        /// <param name="payload">First record byte; caller-owned and pinned.</param>
        /// <param name="length">Bytes available from <paramref name="payload"/>.</param>
        /// <param name="brickCount">Records the header claims. Validated against the bytes.</param>
        public void Add(in VoxelEntityData data, byte* payload, int length, int brickCount)
        {
            CheckNotDisposed();
            if (messages.Length == 0)
            {
                // The previous flush's results stay readable until the next batch starts.
                errors.Clear();
            }

            messages.Add(new Message
            {
                Entity = ResolveEntity(in data),
                Payload = payload,
                Length = length,
                BrickCount = brickCount,
            });
            errors.Add(ReplicatedBrickBatchError.None);
        }

        /// <summary>
        /// Validates, groups, applies and publishes every queued message, then empties the queue.
        /// <see cref="Errors"/> holds one entry per queued message afterwards.
        /// </summary>
        public void Flush()
        {
            CheckNotDisposed();
            if (messages.Length == 0)
            {
                return;
            }

            // Pass 1: validate and group. Reads the entity stores, writes none of them.
            records.Clear();
            work.Clear();
            workIndex.Clear();
            strides.Clear();
            removeCapacity[0] = 0;

            new GroupJob
            {
                Entities = entities.AsArray(),
                Messages = messages.AsArray(),
                Records = records,
                Work = work,
                WorkIndex = workIndex,
                Strides = strides,
                AddedThisMessage = addedThisMessage,
                Errors = errors.AsArray(),
                RemoveCapacity = removeCapacity,
            }.Run();

            // Pass 2: create the storage the records need. Main thread: allocation is topology.
            for (int i = 0; i < work.Length; i++)
            {
                SectorWork item = work[i];
                if (!item.Sector.IsNull)
                {
                    continue;
                }

                VoxelEntityData data = entities[item.Entity].Data;
                data.AddEmptySectorAt(item.SectorPos);
                item.Sector = data.sectors[item.SectorPos];
                work[i] = item;
            }

            removed.Clear();
            if (removed.Capacity < removeCapacity[0])
            {
                removed.Capacity = removeCapacity[0];
            }

            // Pass 3: apply. One work item owns one sector exclusively, so the writes are disjoint.
            if (work.Length > 0)
            {
                var apply = new ApplyJob
                {
                    Work = work.AsArray(),
                    Records = records.AsArray(),
                    Messages = messages.AsArray(),
                    Removed = removed.AsParallelWriter(),
                };

                // A single sector has no independent work: Run still uses Burst and avoids a
                // schedule/wait round trip.
                if (work.Length == 1) apply.Run(1);
                else apply.Schedule(work.Length, 1).Complete();
            }

            // Pass 4: publish. Removals first, so they precede this cycle's updated entries.
            for (int i = 0; i < removed.Length; i++)
            {
                RemovedBrick brick = removed[i];
                entities[brick.Entity].Data.AppendRemovedBrick(brick.Key);
            }

            for (int i = 0; i < work.Length; i++)
            {
                SectorWork item = work[i];
                if (item.Sector.IsNull || item.Sector.Get().NonEmptyBrickCount != 0)
                {
                    continue;
                }

                // Frees the storage and marks the surviving boundary. Appends no change entry:
                // the unit is empty, and its bricks were published above.
                entities[item.Entity].Data.RemoveSectorAt(item.SectorPos);
            }

            for (int i = 0; i < work.Length; i++)
            {
                entities[work[i].Entity].Data.MarkStorageMutated();
            }

            entities.Clear();
            messages.Clear();
            records.Clear();
            work.Clear();
            workIndex.Clear();
            strides.Clear();
            addedThisMessage.Clear();
            removed.Clear();
            lastEntity = -1;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (entities.IsCreated) entities.Dispose();
            if (messages.IsCreated) messages.Dispose();
            if (records.IsCreated) records.Dispose();
            if (work.IsCreated) work.Dispose();
            if (addedThisMessage.IsCreated) addedThisMessage.Dispose();
            if (removed.IsCreated) removed.Dispose();
            if (errors.IsCreated) errors.Dispose();
            if (workIndex.IsCreated) workIndex.Dispose();
            if (strides.IsCreated) strides.Dispose();
            if (removeCapacity.IsCreated) removeCapacity.Dispose();
        }

        /// <summary>
        /// Index of <paramref name="data"/>'s store in <see cref="entities"/>, appending it when it
        /// is new. Copies of one entity share one change log, so that pointer is the identity. The
        /// last resolved entity is tried first: a batch usually arrives entity by entity, and the
        /// number of entities in one flush is small enough that the fallback scan is linear.
        /// </summary>
        private int ResolveEntity(in VoxelEntityData data)
        {
            IntPtr identity = data.ChangeLogIdentity;
            if (lastEntity >= 0 && entities[lastEntity].Data.ChangeLogIdentity == identity)
            {
                return lastEntity;
            }

            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i].Data.ChangeLogIdentity == identity)
                {
                    lastEntity = i;
                    return i;
                }
            }

            entities.Add(new EntityRef { Data = data });
            lastEntity = entities.Length - 1;
            return lastEntity;
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckNotDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BrickBatchApplier));
            }
        }

        /// <summary>
        /// Pass 1: validates every queued message and chains its records into per-sector work
        /// items. Single-threaded because the order of records inside one sector is the wire order.
        /// </summary>
        [BurstCompile]
        internal struct GroupJob : IJob
        {
            [ReadOnly] public NativeArray<EntityRef> Entities;
            [ReadOnly] public NativeArray<Message> Messages;
            public NativeList<Record> Records;
            public NativeList<SectorWork> Work;
            public NativeHashMap<WorkKey, int> WorkIndex;

            /// <summary>Stride declared per (entity, sector, slot) so far this flush.</summary>
            public NativeHashMap<StrideKey, int> Strides;

            /// <summary>Stride keys the current message declared, so an invalid one can undo them.</summary>
            public NativeList<StrideKey> AddedThisMessage;

            public NativeArray<ReplicatedBrickBatchError> Errors;

            /// <summary>Element 0: an upper bound on the removals pass 3 can produce.</summary>
            public NativeArray<int> RemoveCapacity;

            private int removeCount;

            public void Execute()
            {
                removeCount = 0;
                for (int m = 0; m < Messages.Length; m++)
                {
                    int firstRecord = Records.Length;
                    AddedThisMessage.Clear();

                    ReplicatedBrickBatchError error = Validate(m);
                    if (error != ReplicatedBrickBatchError.None)
                    {
                        // An invalid message changes nothing: drop its records and the strides it
                        // declared, and carry on with the next one.
                        Errors[m] = error;
                        Records.Length = firstRecord;
                        for (int i = 0; i < AddedThisMessage.Length; i++)
                        {
                            Strides.Remove(AddedThisMessage[i]);
                        }

                        continue;
                    }

                    Errors[m] = ReplicatedBrickBatchError.None;
                    Link(m, firstRecord);
                }

                RemoveCapacity[0] = removeCount;
            }

            private ReplicatedBrickBatchError Validate(int m)
            {
                Message message = Messages[m];
                byte* payload = message.Payload;
                int length = message.Length;
                int offset = 0;

                if (length < 0 || message.BrickCount < 0)
                {
                    return ReplicatedBrickBatchError.InvalidOp;
                }

                for (int r = 0; r < message.BrickCount; r++)
                {
                    if (length - offset < BrickReplication.RecordHeaderBytes)
                    {
                        return ReplicatedBrickBatchError.Truncated;
                    }

                    int3 key = BrickReplication.ReadUnaligned<int3>(payload + offset);
                    byte op = payload[offset + 12];
                    offset += BrickReplication.RecordHeaderBytes;
                    if (op > (byte)BrickOp.Remove)
                    {
                        return ReplicatedBrickBatchError.InvalidOp;
                    }

                    int recordOffset = offset;
                    if (op == (byte)BrickOp.Update)
                    {
                        if (length - offset < 1)
                        {
                            return ReplicatedBrickBatchError.Truncated;
                        }

                        int slotCount = payload[offset];
                        offset += 1;
                        if (slotCount == 0)
                        {
                            // A zero-slot Update is a no-op by contract. Dropping it HERE, before
                            // it becomes a record, keeps it from creating a work item, a sector
                            // for a key nobody fills, and the boundary invalidation that freeing
                            // that empty sector would cause.
                            continue;
                        }

                        int3 sectorPos = SectorKeys.ToSectorPos(key);
                        for (int s = 0; s < slotCount; s++)
                        {
                            if (length - offset < BrickReplication.SlotRecordHeaderBytes)
                            {
                                return ReplicatedBrickBatchError.Truncated;
                            }

                            int slotId = payload[offset];
                            int stride = BrickReplication.ReadUnaligned<ushort>(payload + offset + 1);
                            offset += BrickReplication.SlotRecordHeaderBytes;
                            if (slotId >= Sector.MAX_SLOTS || stride == 0)
                            {
                                return ReplicatedBrickBatchError.InvalidSlot;
                            }

                            var strideKey = new StrideKey
                            {
                                Entity = message.Entity,
                                SectorPos = sectorPos,
                                SlotId = slotId,
                            };

                            if (!Strides.TryGetValue(strideKey, out int known))
                            {
                                known = ExistingStride(message.Entity, sectorPos, slotId);
                            }

                            if (known != 0 && known != stride)
                            {
                                return ReplicatedBrickBatchError.SlotStrideMismatch;
                            }

                            if (known == 0)
                            {
                                Strides.Add(strideKey, stride);
                                AddedThisMessage.Add(strideKey);
                            }

                            int bytes = stride * Sector.BLOCKS_IN_BRICK;
                            if (length - offset < bytes)
                            {
                                return ReplicatedBrickBatchError.Truncated;
                            }

                            offset += bytes;
                        }
                    }
                    else
                    {
                        removeCount++;
                    }

                    Records.Add(new Record
                    {
                        Message = m,
                        Offset = recordOffset,
                        Key = key,
                        Op = (BrickOp)op,
                        Next = -1,
                    });
                }

                return ReplicatedBrickBatchError.None;
            }

            /// <summary>The stride the entity's storage already uses for that slot, or 0.</summary>
            private int ExistingStride(int entity, int3 sectorPos, int slotId)
            {
                if (!Entities[entity].Data.sectors.TryGetValue(sectorPos, out SectorHandle handle) || handle.IsNull)
                {
                    return 0;
                }

                SectorSlotStorage* slot = handle.Get().slots + slotId;
                return slot->IsCreated ? slot->stride : 0;
            }

            /// <summary>Chains the records of a validated message into their sectors' work items.</summary>
            private void Link(int m, int firstRecord)
            {
                int entity = Messages[m].Entity;
                for (int r = firstRecord; r < Records.Length; r++)
                {
                    int3 key = Records[r].Key;
                    var workKey = new WorkKey { Entity = entity, SectorPos = SectorKeys.ToSectorPos(key) };
                    if (!WorkIndex.TryGetValue(workKey, out int index))
                    {
                        if (!Entities[entity].Data.sectors.TryGetValue(workKey.SectorPos, out SectorHandle handle))
                        {
                            handle = default;
                        }

                        if (handle.IsNull && Records[r].Op == BrickOp.Remove)
                        {
                            // Nothing to remove, and a removal must never create storage.
                            continue;
                        }

                        index = Work.Length;
                        Work.Add(new SectorWork
                        {
                            Entity = entity,
                            SectorPos = workKey.SectorPos,
                            Sector = handle,
                            First = -1,
                            Last = -1,
                        });
                        WorkIndex.Add(workKey, index);
                    }

                    SectorWork item = Work[index];
                    if (item.Last < 0)
                    {
                        item.First = r;
                    }
                    else
                    {
                        Record previous = Records[item.Last];
                        previous.Next = r;
                        Records[item.Last] = previous;
                    }

                    item.Last = r;
                    Work[index] = item;
                }
            }
        }

        /// <summary>
        /// Pass 3: applies one sector's chain of records. Parallel over work items; each owns its
        /// sector exclusively, and different entities never share one.
        /// </summary>
        [BurstCompile]
        internal struct ApplyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SectorWork> Work;
            [ReadOnly] public NativeArray<Record> Records;
            [ReadOnly] public NativeArray<Message> Messages;
            public NativeList<RemovedBrick>.ParallelWriter Removed;

            public void Execute(int index)
            {
                SectorWork item = Work[index];
                if (item.Sector.IsNull)
                {
                    return;
                }

                ref Sector sector = ref item.Sector.Get();
                bool allocationChanged = false;

                for (int r = item.First; r >= 0; r = Records[r].Next)
                {
                    Record record = Records[r];
                    byte* p = Messages[record.Message].Payload + record.Offset;
                    int brickIdx = SectorKeys.ToBrickIdx(record.Key);

                    if (record.Op == BrickOp.Update)
                    {
                        int slotCount = p[0];
                        if (slotCount == 0)
                        {
                            continue; // zero slots is a no-op, not a removal
                        }

                        allocationChanged |= sector.ApplyReplicatedBrick(brickIdx, p + 1, slotCount);
                    }
                    else if (sector.RemoveReplicatedBrick(brickIdx))
                    {
                        Removed.AddNoResize(new RemovedBrick { Entity = item.Entity, Key = record.Key });
                        allocationChanged = true;
                    }
                }

                // A removal plus an addition leaves the count equal with a stale list, so this keys
                // on any allocation change rather than on the count.
                if (allocationChanged)
                {
                    sector.UpdateNonEmptyBricks();
                }
            }
        }
    }
}

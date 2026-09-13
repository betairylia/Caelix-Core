using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// Wire codec: message writer and reader, the in-process channel, and the keyed brick
    /// replication codec that every server-to-client voxel update goes through.
    /// </summary>
    public unsafe class NetCodecTests
    {
        private const ushort BlockOnly = BrickReplication.DefaultReplicatedSlotMask;
        private const ushort AllSlots = 0xFFFF;

        // ---- Helpers -----------------------------------------------------------------------

        /// <summary>The exact bytes one keyed record of <paramref name="source"/> occupies on the wire.</summary>
        private static byte[] Record(VoxelEntityData source, int3 key, BrickOp op, ushort slotMask)
        {
            var bytes = new byte[source.ReplicatedRecordBytes(key, op, slotMask)];
            fixed (byte* ptr = bytes)
            {
                var writer = new UnsafeNetWriter(ptr, bytes.Length);
                source.WriteReplicatedRecord(ref writer, key, op, slotMask);
                Assert.That(writer.Length, Is.EqualTo(bytes.Length), "record size and record write disagree");
            }

            return bytes;
        }

        /// <summary>Applies one message through a fresh applier and returns its validation result.</summary>
        private static ReplicatedBrickBatchError ApplyBatch(
            in VoxelEntityData data, byte[] payload, int length, int brickCount)
        {
            using var applier = new BrickBatchApplier();
            fixed (byte* ptr = payload)
            {
                applier.Add(in data, ptr, length, brickCount);
                applier.Flush();
                return applier.Errors[0];
            }
        }

        private static ReplicatedBrickBatchError ApplyBatch(in VoxelEntityData data, byte[] payload, int brickCount)
            => ApplyBatch(in data, payload, payload.Length, brickCount);

        private static int BrickCount(VoxelEntityData data)
        {
            int count = 0;
            foreach (int3 _ in data.EnumerateBricks()) count++;
            return count;
        }

        private static ushort DirtyAt(VoxelEntityData data, int3 key)
        {
            SectorHandle handle = data.sectors[key >> Sector.SHIFT_IN_BRICKS];
            int brickIdx = Sector.ToBrickIdx(
                key.x & Sector.SECTOR_MASK, key.y & Sector.SECTOR_MASK, key.z & Sector.SECTOR_MASK);
            return handle.Get().brickDirtyFlags[brickIdx];
        }

        private static ushort RequireAt(VoxelEntityData data, int3 key)
        {
            SectorHandle handle = data.sectors[key >> Sector.SHIFT_IN_BRICKS];
            int brickIdx = Sector.ToBrickIdx(
                key.x & Sector.SECTOR_MASK, key.y & Sector.SECTOR_MASK, key.z & Sector.SECTOR_MASK);
            return handle.Get().brickRequireUpdateFlags[brickIdx];
        }

        // ---- Records -----------------------------------------------------------------------

        [Test]
        public void Record_RoundTripsBlockSlotOnly()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var target = new VoxelEntityData(Allocator.Persistent);
            var scratch = new VoxelEntityData(Allocator.Persistent);
            try
            {
                source.SetBlock(new int3(9, 10, 11), new Block(0x8001));
                source.SetBlock(new int3(15, 8, 8), new Block(0x8002));
                // A second, non-replicated slot in the same brick.
                source.SetSlot((SectorSlotId)2, new int3(9, 10, 11), (short)42);

                var key = new int3(1, 1, 1);
                byte[] record = Record(source, key, BrickOp.Update, BlockOnly);

                Assert.That(ApplyBatch(in target, record, 1), Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(target.GetBlock(new int3(9, 10, 11)), Is.EqualTo(new Block(0x8001)));
                Assert.That(target.GetBlock(new int3(15, 8, 8)), Is.EqualTo(new Block(0x8002)));
                Assert.That(target.GetBlock(new int3(8, 8, 8)).isEmpty, Is.True);
                Assert.That(BrickCount(target), Is.EqualTo(1));
                Assert.That(target.sectors[int3.zero].Get().slots[2].IsCreated, Is.False,
                    "non-replicated slot must not arrive");

                ushort dirty = DirtyAt(target, key);
                Assert.That(dirty & (ushort)BrickUpdateFlags.Geometry, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)BrickUpdateFlags.BlockBrickAdded, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)BrickUpdateFlags.Automata0, Is.EqualTo(0),
                    "replica must not trigger automata");

                // The record is exact: one byte short of it is a truncated message that applies nothing.
                Assert.That(ApplyBatch(in scratch, record, record.Length - 1, 1),
                    Is.EqualTo(ReplicatedBrickBatchError.Truncated));
                Assert.That(scratch.sectors.Count, Is.Zero);
            }
            finally
            {
                source.Dispose();
                target.Dispose();
                scratch.Dispose();
            }
        }

        [Test]
        public void Record_SecondApplyOverwritesWithoutBrickAdded()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var target = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var key = int3.zero;
                source.SetBlock(new int3(1, 1, 1), new Block(0x8001));
                Assert.That(ApplyBatch(in target, Record(source, key, BrickOp.Update, BlockOnly), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));
                target.ClearDirtyFlags();

                source.SetBlock(new int3(1, 1, 1), Block.Empty);
                source.SetBlock(new int3(2, 2, 2), new Block(0x8003));
                Assert.That(ApplyBatch(in target, Record(source, key, BrickOp.Update, BlockOnly), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));

                Assert.That(target.GetBlock(new int3(1, 1, 1)).isEmpty, Is.True);
                Assert.That(target.GetBlock(new int3(2, 2, 2)), Is.EqualTo(new Block(0x8003)));
                Assert.That(DirtyAt(target, key) & (ushort)BrickUpdateFlags.BlockBrickAdded, Is.EqualTo(0));
                Assert.That(DirtyAt(target, key) & (ushort)BrickUpdateFlags.Geometry, Is.Not.EqualTo(0));
            }
            finally
            {
                source.Dispose();
                target.Dispose();
            }
        }

        // ---- Batches -----------------------------------------------------------------------

        [Test]
        public void Batch_MatchesPerRecordApplyWithMultipleSlotsAndRepeatedKeys()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var expected = new VoxelEntityData(Allocator.Persistent);
            var actual = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var batch = new NetMessageWriter();
                batch.Write((byte)99); // deliberately unaligned input
                int count = 0;

                for (int b = 0; b < 40; b++)
                {
                    int i = b % 31;
                    int3 key = new int3(i & 3, (i >> 2) & 3, 0) + ((b & 1) == 0 ? int3.zero : new int3(16, 0, 0));
                    int3 block = BrickKey.ToBlockOrigin(key) + new int3(1, 2, 3);

                    source.SetBlock(block, new Block((ushort)(0x8001 + b)));
                    source.SetSlot((SectorSlotId)2, block, b + 123);

                    byte[] record = Record(source, key, BrickOp.Update, AllSlots);
                    Assert.That(ApplyBatch(in expected, record, 1), Is.EqualTo(ReplicatedBrickBatchError.None));
                    batch.WriteBytes(record);
                    count++;
                }

                // A trailing zero-slot Update: a no-op that must not change or free anything.
                byte[] tail = Record(source, new int3(5, 5, 5), BrickOp.Update, AllSlots);
                Assert.That(tail.Length, Is.EqualTo(BrickReplication.RecordHeaderBytes + 1));
                Assert.That(ApplyBatch(in expected, tail, 1), Is.EqualTo(ReplicatedBrickBatchError.None));
                batch.WriteBytes(tail);
                count++;

                byte[] payload = new byte[batch.Length - 1];
                System.Buffer.BlockCopy(batch.Buffer, 1, payload, 0, payload.Length);
                Assert.That(ApplyBatch(in actual, payload, count), Is.EqualTo(ReplicatedBrickBatchError.None));

                AssertStorageEqual(expected, actual);
            }
            finally
            {
                source.Dispose();
                expected.Dispose();
                actual.Dispose();
            }
        }

        [TestCase(ReplicatedBrickBatchError.Truncated)]
        [TestCase(ReplicatedBrickBatchError.InvalidOp)]
        [TestCase(ReplicatedBrickBatchError.InvalidSlot)]
        [TestCase(ReplicatedBrickBatchError.SlotStrideMismatch)]
        public void Batch_InvalidSecondRecordDoesNotApplyFirst(ReplicatedBrickBatchError error)
        {
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var writer = new NetMessageWriter();
                for (int b = 0; b < 2; b++)
                {
                    bool bad = b == 1;
                    writer.Write(new int3(b, 0, 0));
                    writer.Write((byte)(bad && error == ReplicatedBrickBatchError.InvalidOp ? 7 : 0));
                    writer.Write((byte)1);
                    writer.Write((byte)(bad && error == ReplicatedBrickBatchError.InvalidSlot ? 255 : 0));
                    int stride = bad && error == ReplicatedBrickBatchError.SlotStrideMismatch ? 4 : 2;
                    writer.Write((ushort)stride);
                    writer.WriteBytes(new byte[Sector.BLOCKS_IN_BRICK * stride]);
                }

                byte[] payload = writer.AsSpan().ToArray();
                int length = payload.Length - (error == ReplicatedBrickBatchError.Truncated ? 1 : 0);
                Assert.That(ApplyBatch(in data, payload, length, 2), Is.EqualTo(error));
                Assert.That(data.sectors.Count, Is.Zero, "an invalid message must not create storage");
            }
            finally
            {
                data.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Batch_ExistingStoragePreservesStrideAndDirtySemantics(bool mismatch)
        {
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                data.SetBlock(new int3(1, 1, 1), new Block(0x8001));
                data.ClearDirtyFlags();

                var writer = new NetMessageWriter();
                writer.Write(int3.zero);
                writer.Write((byte)BrickOp.Update);
                writer.Write((byte)1);
                writer.Write((byte)SectorSlotId.Block);
                int stride = mismatch ? 4 : 2;
                writer.Write((ushort)stride);
                writer.WriteBytes(new byte[Sector.BLOCKS_IN_BRICK * stride]);

                Assert.That(ApplyBatch(in data, writer.AsSpan().ToArray(), 1),
                    Is.EqualTo(mismatch ? ReplicatedBrickBatchError.SlotStrideMismatch : ReplicatedBrickBatchError.None));
                Assert.That(data.GetBlock(new int3(1, 1, 1)),
                    Is.EqualTo(mismatch ? new Block(0x8001) : Block.Empty));
                Assert.That(BrickCount(data), Is.EqualTo(1));
                Assert.That(DirtyAt(data, int3.zero),
                    Is.EqualTo(mismatch ? 0 : (ushort)BrickReplication.ReplicationApplyFlags));

                data.ClearDirtyFlags();

                // Zero-slot records must preserve an allocated brick, its contents and its flags.
                writer.Reset();
                writer.Write(int3.zero);
                writer.Write((byte)BrickOp.Update);
                writer.Write((byte)0);
                Assert.That(ApplyBatch(in data, writer.AsSpan().ToArray(), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(data.GetBlock(new int3(1, 1, 1)),
                    Is.EqualTo(mismatch ? new Block(0x8001) : Block.Empty));
                Assert.That(BrickCount(data), Is.EqualTo(1));
                Assert.That(data.sectors[int3.zero].Get().sectorDirtyFlags, Is.Zero);
            }
            finally
            {
                data.Dispose();
            }
        }

        [Test]
        public void Batch_MessagesForOneEntityApplyInReceiveOrder()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var target = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var key = new int3(1, 1, 1);
                var block = new int3(9, 9, 9);
                var writer = new NetMessageWriter();
                var offsets = new int[3];
                var lengths = new int[3];

                for (int i = 0; i < 3; i++)
                {
                    source.SetBlock(block, new Block((ushort)(0x8001 + i)));
                    byte[] record = Record(source, key, BrickOp.Update, BlockOnly);
                    offsets[i] = writer.Length;
                    lengths[i] = record.Length;
                    writer.WriteBytes(record);
                }

                byte[] payload = writer.AsSpan().ToArray();
                using (var applier = new BrickBatchApplier())
                fixed (byte* ptr = payload)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        applier.Add(in target, ptr + offsets[i], lengths[i], 1);
                    }

                    Assert.That(applier.MessageCount, Is.EqualTo(3));
                    applier.Flush();
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(applier.Errors[i], Is.EqualTo(ReplicatedBrickBatchError.None));
                    }
                }

                Assert.That(target.GetBlock(block), Is.EqualTo(new Block(0x8003)), "the last message wins");
            }
            finally
            {
                source.Dispose();
                target.Dispose();
            }
        }

        // ---- Removals ----------------------------------------------------------------------

        [Test]
        public void Batch_RemoveFreesBrickPublishesRemovedAndMarksNeighbours()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var removedKey = new int3(1, 1, 1);
                var neighbourKey = new int3(2, 1, 1);
                data.SetBlock(BrickKey.ToBlockOrigin(removedKey), new Block(0x8001));
                data.SetBlock(BrickKey.ToBlockOrigin(neighbourKey), new Block(0x8002));
                data.ClearDirtyFlags();
                Assert.That(data.ChangeCount, Is.Zero);

                Assert.That(ApplyBatch(in data, Record(source, removedKey, BrickOp.Remove, BlockOnly), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));

                Assert.That(data.IsBrickAllocated(removedKey), Is.False);
                Assert.That(data.sectors.ContainsKey(int3.zero), Is.True, "the sector still holds another brick");
                Assert.That(data.ChangeCount, Is.EqualTo(1));

                BrickChange change = data.GetChange(0);
                Assert.That(change.Kind, Is.EqualTo(ChangeKind.Removed));
                Assert.That(change.Key, Is.EqualTo(removedKey));

                ushort dirty = DirtyAt(data, removedKey);
                Assert.That(dirty & (ushort)BrickUpdateFlags.BlockBrickRemoved, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));

                data.PropagateDirtyFlags(BrickUpdateFlags.GeometryWithLocalNeighbor).Complete();
                Assert.That(RequireAt(data, neighbourKey) & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor,
                    Is.Not.EqualTo(0), "a freed brick index still re-renders its neighbours");
            }
            finally
            {
                source.Dispose();
                data.Dispose();
            }
        }

        [Test]
        public void Batch_RemovingLastBrickFreesSectorAndMarksSurvivingBoundary()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var survivor = new int3(15, 0, 0);   // last brick of sector (0,0,0)
                var removedKey = new int3(16, 0, 0); // first brick of sector (1,0,0)
                data.SetBlock(BrickKey.ToBlockOrigin(survivor), new Block(0x8001));
                data.SetBlock(BrickKey.ToBlockOrigin(removedKey), new Block(0x8002));
                data.ClearDirtyFlags();

                Assert.That(ApplyBatch(in data, Record(source, removedKey, BrickOp.Remove, BlockOnly), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));

                Assert.That(data.sectors.ContainsKey(new int3(1, 0, 0)), Is.False, "an emptied sector is freed");
                Assert.That(data.ChangeCount, Is.EqualTo(1));
                Assert.That(data.GetChange(0).Kind, Is.EqualTo(ChangeKind.Removed));
                Assert.That(data.GetChange(0).Key, Is.EqualTo(removedKey));
                Assert.That(DirtyAt(data, survivor) & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor,
                    Is.Not.EqualTo(0), "the surviving boundary was marked by the sector removal");
            }
            finally
            {
                source.Dispose();
                data.Dispose();
            }
        }

        [Test]
        public void Batch_RemoveThenUpdateSameKeyInOneMessageEndsAllocated()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                var key = new int3(1, 1, 1);
                var block = BrickKey.ToBlockOrigin(key) + new int3(2, 3, 4);
                data.SetBlock(block, new Block(0x8001));
                data.ClearDirtyFlags();

                source.SetBlock(block, new Block(0x8007));
                byte[] remove = Record(source, key, BrickOp.Remove, BlockOnly);
                byte[] update = Record(source, key, BrickOp.Update, BlockOnly);

                var writer = new NetMessageWriter();
                writer.WriteBytes(remove);
                writer.WriteBytes(update);
                Assert.That(ApplyBatch(in data, writer.AsSpan().ToArray(), 2),
                    Is.EqualTo(ReplicatedBrickBatchError.None));

                Assert.That(data.IsBrickAllocated(key), Is.True);
                Assert.That(data.GetBlock(block), Is.EqualTo(new Block(0x8007)));
                Assert.That(data.ChangeCount, Is.EqualTo(1));
                Assert.That(data.GetChange(0).Kind, Is.EqualTo(ChangeKind.Removed));
                Assert.That(data.GetChange(0).Key, Is.EqualTo(key));

                data.BuildChangeList();
                Assert.That(data.ChangeCount, Is.EqualTo(2));
                BrickChange updated = data.GetChange(1);
                Assert.That(updated.Kind, Is.EqualTo(ChangeKind.Updated));
                Assert.That(updated.Key, Is.EqualTo(key));
                Assert.That(updated.DirtyFlags & BrickUpdateFlags.BlockBrickAdded, Is.Not.EqualTo(BrickUpdateFlags.None));
            }
            finally
            {
                source.Dispose();
                data.Dispose();
            }
        }

        [Test]
        public void Batch_RemoveOfUnknownKeyIsNoOp()
        {
            var source = new VoxelEntityData(Allocator.Persistent);
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                Assert.That(ApplyBatch(in data, Record(source, new int3(40, 1, 2), BrickOp.Remove, BlockOnly), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(data.sectors.Count, Is.Zero);
                Assert.That(data.ChangeCount, Is.Zero);
            }
            finally
            {
                source.Dispose();
                data.Dispose();
            }
        }

        [Test]
        public void Batch_ZeroSlotUpdateCreatesNoSectorAndInvalidatesNothing()
        {
            var data = new VoxelEntityData(Allocator.Persistent);
            try
            {
                // A neighbour sector with one brick on the face the empty key would touch. If the
                // zero-slot record created and then freed a sector, RemoveSectorAt would mark this
                // brick GeometryWithLocalNeighbor.
                var neighbourKey = new int3(15, 4, 5);
                data.SetBlock(BrickKey.ToBlockOrigin(neighbourKey), new Block(0x8001));
                data.ClearDirtyFlags();

                var writer = new NetMessageWriter();
                writer.Write(new int3(16, 4, 5));
                writer.Write((byte)BrickOp.Update);
                writer.Write((byte)0);

                Assert.That(ApplyBatch(in data, writer.AsSpan().ToArray(), 1),
                    Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(data.sectors.Count, Is.EqualTo(1), "a no-op record must not create a sector");
                Assert.That(data.sectors.ContainsKey(new int3(1, 0, 0)), Is.False);
                Assert.That(data.ChangeCount, Is.Zero);
                Assert.That(DirtyAt(data, neighbourKey), Is.Zero, "a no-op record must not invalidate neighbours");
            }
            finally
            {
                data.Dispose();
            }
        }

        /// <summary>Compares two replicas brick for brick: contents, bounds and dirty flags.</summary>
        private static void AssertStorageEqual(VoxelEntityData expected, VoxelEntityData actual)
        {
            Assert.That(actual.sectors.Count, Is.EqualTo(expected.sectors.Count));
            Assert.That(BrickCount(actual), Is.EqualTo(BrickCount(expected)));

            foreach (var entry in expected.sectors)
            {
                Assert.That(actual.sectors.TryGetValue(entry.Key, out SectorHandle actualHandle), Is.True);
                ref Sector expectedSector = ref entry.Value.Get();
                ref Sector actualSector = ref actualHandle.Get();
                Assert.That(actualSector.blockAABB, Is.EqualTo(expectedSector.blockAABB));

                foreach (SectorNonEmptyBrickEnumerator.BrickRef brick in expectedSector.EnumerateNonEmptyBricks())
                {
                    short expectedId = expectedSector.brickIdx[brick.BrickAbs];
                    short actualId = actualSector.brickIdx[brick.BrickAbs];
                    Assert.That(actualId, Is.Not.EqualTo(Sector.BRICKID_EMPTY));

                    for (int slot = 0; slot < Sector.MAX_SLOTS; slot++)
                    {
                        Assert.That(actualSector.slots[slot].IsCreated,
                            Is.EqualTo(expectedSector.slots[slot].IsCreated), $"slot {slot} existence");
                        if (!expectedSector.slots[slot].IsCreated) continue;

                        Assert.That(
                            UnsafeUtility.MemCmp(
                                actualSector.slots[slot].GetBrickPtr(actualId),
                                expectedSector.slots[slot].GetBrickPtr(expectedId),
                                expectedSector.slots[slot].BytesPerBrick),
                            Is.Zero, $"slot {slot} of brick {brick.BrickAbs}");
                    }

                    Assert.That(actualSector.brickDirtyFlags[brick.BrickAbs],
                        Is.EqualTo(expectedSector.brickDirtyFlags[brick.BrickAbs]));
                }
            }
        }

        // ---- Writer, reader, channel, registry ---------------------------------------------

        [Test]
        public void WriterReader_RoundTripsMixedValues()
        {
            var writer = new NetMessageWriter(8);
            var guid = new Guid128(1u, 2u, 3u, 4u);
            var pose = new RigidTransform(quaternion.EulerXYZ(0.1f, 0.2f, 0.3f), new float3(1f, 2f, 3f));

            writer.Write((byte)7);
            writer.Write((ushort)513);
            writer.Write(guid);
            writer.Write(pose);
            int patchOffset = writer.Reserve<uint>();
            writer.Write(new int3(-1, 2, -3));
            writer.Patch(patchOffset, 0xDEADBEEFu);

            var reader = new NetMessageReader(writer.Buffer, writer.Length);
            Assert.That(reader.Read<byte>(), Is.EqualTo(7));
            Assert.That(reader.Read<ushort>(), Is.EqualTo(513));
            Assert.That(reader.Read<Guid128>(), Is.EqualTo(guid));
            RigidTransform readPose = reader.Read<RigidTransform>();
            Assert.That(math.all(readPose.pos == pose.pos), Is.True);
            Assert.That(math.all(readPose.rot.value == pose.rot.value), Is.True);
            Assert.That(reader.Read<uint>(), Is.EqualTo(0xDEADBEEFu));
            Assert.That(reader.Read<int3>(), Is.EqualTo(new int3(-1, 2, -3)));
            Assert.That(reader.Remaining, Is.EqualTo(0));
        }

        [Test]
        public void Reader_ThrowsOnTruncatedMessage()
        {
            var writer = new NetMessageWriter();
            writer.Write((ushort)1);
            var reader = new NetMessageReader(writer.Buffer, writer.Length);
            Assert.Throws<System.IO.EndOfStreamException>(() => reader.Read<uint>());
        }

        [Test]
        public void LocalChannel_DeliversInOrderAndCopies()
        {
            LocalChannel.CreatePair(out LocalChannel a, out LocalChannel b);
            try
            {
                var writer = new NetMessageWriter();
                for (int i = 0; i < 3; i++)
                {
                    writer.Reset();
                    writer.Write(i);
                    a.Send(NetDelivery.Reliable, writer.AsSpan());
                }

                Assert.That(b.PendingCount, Is.EqualTo(3));
                Assert.That(a.PendingCount, Is.EqualTo(0));

                for (int i = 0; i < 3; i++)
                {
                    Assert.That(b.TryReceive(out byte[] message), Is.True);
                    var reader = new NetMessageReader(message);
                    Assert.That(reader.Read<int>(), Is.EqualTo(i));
                }

                Assert.That(b.TryReceive(out _), Is.False);
            }
            finally
            {
                a.Dispose();
                b.Dispose();
            }
        }

        [Test]
        public void TypeRegistry_AssignsIdsInOrderAndHashesConsistently()
        {
            var first = new NetTypeRegistry();
            var second = new NetTypeRegistry();
            Assert.That(first.Register<int3>(), Is.EqualTo(0));
            Assert.That(first.Register<Guid128>(), Is.EqualTo(1));
            Assert.That(first.Register<int3>(), Is.EqualTo(0));
            second.Register<int3>();
            second.Register<Guid128>();
            Assert.That(second.Hash, Is.EqualTo(first.Hash));

            var reordered = new NetTypeRegistry();
            reordered.Register<Guid128>();
            reordered.Register<int3>();
            Assert.That(reordered.Hash, Is.Not.EqualTo(first.Hash));
        }

        [Test]
        public void LocalChannel_SelectiveReceiveLeavesOtherMessagesInOrder()
        {
            LocalChannel.CreatePair(out LocalChannel a, out LocalChannel b);
            using (a)
            using (b)
            {
                for (byte i = 0; i < 6; i++) a.Send(NetDelivery.Reliable, new byte[] { i });
                Assert.That(b.TryReceive(m => m[0] == 3, out byte[] query), Is.True);
                Assert.That(query[0], Is.EqualTo(3));
                Assert.That(b.TryReceive(m => m[0] == 9, out _), Is.False);
                Assert.That(b.PendingCount, Is.EqualTo(5));
                Assert.That(b.PendingBytes, Is.EqualTo(5));
                Assert.That(b.PeakPendingBytes, Is.EqualTo(6));
                foreach (byte expected in new byte[] { 0, 1, 2, 4, 5 })
                {
                    Assert.That(b.TryReceive(out byte[] message), Is.True);
                    Assert.That(message[0], Is.EqualTo(expected));
                }
                Assert.That(b.PendingBytes, Is.Zero);
                Assert.That(b.TotalReceivedBytes, Is.EqualTo(6));
            }
        }

        [Test]
        public void SectorAttachment_RecreationHasNewIdentityAcrossEntityCopies()
        {
            var entity = new VoxelEntityData(Allocator.Persistent);
            try
            {
                entity.AddEmptySectorAt(int3.zero);
                long original = entity.sectors[int3.zero].InstanceId;
                var copy = entity;
                copy.RemoveSectorAt(int3.zero);
                copy.AddEmptySectorAt(int3.zero);
                Assert.That(entity.sectors[int3.zero].InstanceId, Is.Not.EqualTo(original));
                Assert.That(entity.sectors[int3.zero].InstanceId, Is.EqualTo(copy.sectors[int3.zero].InstanceId));
            }
            finally
            {
                entity.Dispose();
            }
        }

        [Test]
        public void VoxelSlots_WriteSelectsExistingMaskedSlots()
        {
            SectorHandle handle = SectorHandle.AllocEmpty();
            try
            {
                ref Sector sector = ref handle.Get();
                sector.SetBlock(3, 4, 5, new Block(0x8007));
                sector.SetVoxelSlot((SectorSlotId)2, 3, 4, 5, (short)-9);

                var writer = new NetMessageWriter();
                sector.WriteVoxelSlots(writer, 3, 4, 5, 0xFFFF);

                var reader = new NetMessageReader(writer.Buffer, writer.Length);
                int count = reader.Read<byte>();
                Assert.That(count, Is.EqualTo(2));

                Assert.That(reader.Read<byte>(), Is.EqualTo(0));
                Assert.That(reader.Read<ushort>(), Is.EqualTo(2));
                Assert.That(reader.Read<Block>(), Is.EqualTo(new Block(0x8007)));

                Assert.That(reader.Read<byte>(), Is.EqualTo(2));
                Assert.That(reader.Read<ushort>(), Is.EqualTo(2));
                Assert.That(reader.Read<short>(), Is.EqualTo(-9));
                Assert.That(reader.Remaining, Is.EqualTo(0));
            }
            finally
            {
                handle.Dispose(Allocator.Persistent);
            }
        }
    }
}

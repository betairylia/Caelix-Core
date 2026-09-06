using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Caelix.Net;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// Wire codec: message writer and reader, the in-process channel, and the brick-level
    /// replication codec that every server-to-client voxel update goes through.
    /// </summary>
    public unsafe class NetCodecTests
    {
        [BurstCompile(CompileSynchronously = true)]
        private struct ApplyBatchJob : IJob
        {
            public SectorHandle Target;
            [NativeDisableUnsafePtrRestriction] public byte* Payload;
            public int Length, Count;
            public NativeArray<ReplicatedBrickBatchError> Result;
            public void Execute() => Result[0] = Target.Get().ApplyReplicatedBrickBatch(Payload, Length, Count);
        }

        [Test]
        public void NativeBrickBatch_MatchesManagedCodecWithMultipleSlotsAndRepeatedBricks()
        {
            SectorHandle source = SectorHandle.AllocEmpty();
            SectorHandle expected = SectorHandle.AllocEmpty();
            SectorHandle actual = SectorHandle.AllocEmpty();
            try
            {
                var writer = new NetMessageWriter();
                writer.Write((byte)99); // deliberately unaligned input
                for (int b = 0; b < 40; b++)
                {
                    int index = b % 31;
                    int3 p = Sector.ToBrickPos((short)index) * Sector.SIZE_IN_BLOCKS;
                    source.Get().SetBlock(p.x, p.y, p.z, new Block((ushort)(0x8001 + b)));
                    source.Get().SetVoxelSlot((SectorSlotId)2, p.x, p.y, p.z, b + 123);
                    var bytes = new byte[source.Get().ReplicatedBrickBytes(0xffff)];
                    fixed (byte* ptr = bytes)
                    {
                        var rawWriter = new UnsafeNetWriter(ptr, bytes.Length);
                        source.Get().WriteReplicatedBrick(ref rawWriter, index, 0xffff);
                        var reader = new NetMessageReader(bytes, rawWriter.Length);
                        expected.Get().ApplyReplicatedBrick(index, ref reader);
                        writer.Write((ushort)index);
                        writer.Write((ushort)DirtyFlags.GeneralAutomata);
                        writer.WriteBytes(bytes.AsSpan(0, rawWriter.Length));
                    }
                }
                writer.Write((ushort)100);
                writer.Write((ushort)0);
                writer.Write((byte)0); // unallocated brick, no-op
                using var result = new NativeArray<ReplicatedBrickBatchError>(1, Allocator.TempJob);
                fixed (byte* ptr = writer.Buffer)
                    new ApplyBatchJob { Target = actual, Payload = ptr + 1, Length = writer.Length - 1,
                        Count = 41, Result = result }.Schedule().Complete();
                Assert.That(result[0], Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(actual.Get().brickMap.Count, Is.EqualTo(expected.Get().brickMap.Count));
                Assert.That(actual.Get().blockAABB, Is.EqualTo(expected.Get().blockAABB));
                foreach (var brick in expected.Get().EnumerateNonEmptyBricks())
                {
                    int index = brick.BrickAbs;
                    short aid = actual.Get().brickMap.indices[index];
                    short eid = expected.Get().brickMap.indices[index];
                    foreach (int slot in new[] { 0, 2 })
                        Assert.That(UnsafeUtility.MemCmp(actual.Get().slots[slot].GetBrickPtr(aid),
                            expected.Get().slots[slot].GetBrickPtr(eid), expected.Get().slots[slot].BytesPerBrick), Is.Zero);
                    Assert.That(actual.Get().brickDirtyFlags[index], Is.EqualTo(expected.Get().brickDirtyFlags[index]));
                }
            }
            finally
            {
                source.Dispose(Allocator.Persistent);
                expected.Dispose(Allocator.Persistent);
                actual.Dispose(Allocator.Persistent);
            }
        }

        [TestCase(ReplicatedBrickBatchError.Truncated)]
        [TestCase(ReplicatedBrickBatchError.InvalidBrickIndex)]
        [TestCase(ReplicatedBrickBatchError.InvalidSlot)]
        [TestCase(ReplicatedBrickBatchError.SlotStrideMismatch)]
        public void NativeBrickBatch_InvalidSecondRecordDoesNotApplyFirst(ReplicatedBrickBatchError error)
        {
            SectorHandle target = SectorHandle.AllocEmpty();
            try
            {
                var writer = new NetMessageWriter();
                for (int b = 0; b < 2; b++)
                {
                    writer.Write((ushort)(b == 1 && error == ReplicatedBrickBatchError.InvalidBrickIndex ? 4096 : b));
                    writer.Write((ushort)0);
                    writer.Write((byte)1);
                    writer.Write((byte)(b == 1 && error == ReplicatedBrickBatchError.InvalidSlot ? 255 : 0));
                    writer.Write((ushort)(b == 1 && error == ReplicatedBrickBatchError.SlotStrideMismatch ? 4 : 2));
                    writer.WriteBytes(new byte[Sector.BLOCKS_IN_BRICK * 2]);
                }
                using var result = new NativeArray<ReplicatedBrickBatchError>(1, Allocator.TempJob);
                fixed (byte* ptr = writer.Buffer)
                    new ApplyBatchJob { Target = target, Payload = ptr,
                        Length = writer.Length - (error == ReplicatedBrickBatchError.Truncated ? 1 : 0),
                        Count = 2, Result = result }.Schedule().Complete();
                Assert.That(result[0], Is.EqualTo(error));
                Assert.That(target.Get().brickMap.Count, Is.Zero);
                Assert.That(target.Get().sectorDirtyFlags, Is.Zero);
            }
            finally { target.Dispose(Allocator.Persistent); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeBrickBatch_ExistingStoragePreservesStrideAndDirtySemantics(bool mismatch)
        {
            SectorHandle target = SectorHandle.AllocEmpty();
            try
            {
                target.Get().SetBlock(1, 1, 1, new Block(0x8001));
                target.Get().ClearAllDirtyFlags();
                var writer = new NetMessageWriter();
                writer.Write((ushort)0);
                writer.Write((ushort)DirtyFlags.GeneralAutomata);
                writer.Write((byte)1);
                writer.Write((byte)SectorSlotId.Block);
                int stride = mismatch ? 4 : 2;
                writer.Write((ushort)stride);
                writer.WriteBytes(new byte[Sector.BLOCKS_IN_BRICK * stride]);
                using var result = new NativeArray<ReplicatedBrickBatchError>(1, Allocator.TempJob);
                fixed (byte* ptr = writer.Buffer)
                    new ApplyBatchJob { Target = target, Payload = ptr, Length = writer.Length,
                        Count = 1, Result = result }.Schedule().Complete();
                Assert.That(result[0], Is.EqualTo(mismatch ? ReplicatedBrickBatchError.SlotStrideMismatch : ReplicatedBrickBatchError.None));
                Assert.That(target.Get().GetBlock(1, 1, 1), Is.EqualTo(mismatch ? new Block(0x8001) : Block.Empty));
                Assert.That(target.Get().brickMap.Count, Is.EqualTo(1));
                Assert.That(target.Get().brickDirtyFlags[0], Is.EqualTo(mismatch ? 0 : (ushort)Sector.ReplicationApplyFlags));
                target.Get().ClearAllDirtyFlags();
                // Zero-slot records must preserve an allocated brick, its contents and flags.
                writer.Reset();
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((byte)0);
                fixed (byte* ptr = writer.Buffer)
                    new ApplyBatchJob { Target = target, Payload = ptr, Length = writer.Length,
                        Count = 1, Result = result }.Schedule().Complete();
                Assert.That(result[0], Is.EqualTo(ReplicatedBrickBatchError.None));
                Assert.That(target.Get().GetBlock(1, 1, 1), Is.EqualTo(mismatch ? new Block(0x8001) : Block.Empty));
                Assert.That(target.Get().brickMap.Count, Is.EqualTo(1));
                Assert.That(target.Get().sectorDirtyFlags, Is.Zero);
            }
            finally { target.Dispose(Allocator.Persistent); }
        }

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
                Assert.That(b.TryReceive_RotateQueue(m => m[0] == 3, out byte[] query), Is.True);
                Assert.That(query[0], Is.EqualTo(3));
                Assert.That(b.TryReceive_RotateQueue(m => m[0] == 9, out _), Is.False);
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
        public void ReplicatedBrick_RoundTripsBlockSlotOnly()
        {
            SectorHandle source = SectorHandle.AllocEmpty();
            SectorHandle target = SectorHandle.AllocEmpty();
            try
            {
                ref Sector src = ref source.Get();
                src.SetBlock(9, 10, 11, new Block(0x8001));
                src.SetBlock(15, 8, 8, new Block(0x8002));
                // A second, non-replicated slot in the same brick.
                src.SetVoxelSlot((SectorSlotId)2, 9, 10, 11, (short)42);

                int brickIdx = Sector.ToBrickIdx(1, 1, 1);
                byte[] buffer = new byte[src.ReplicatedBrickBytes(Sector.DefaultReplicatedSlotMask)];
                int written;
                fixed (byte* dstPtr = buffer)
                {
                    var writer = new UnsafeNetWriter(dstPtr, buffer.Length);
                    src.WriteReplicatedBrick(ref writer, brickIdx, Sector.DefaultReplicatedSlotMask);
                    written = writer.Length;
                }

                ref Sector dst = ref target.Get();
                var reader = new NetMessageReader(buffer, written);
                dst.ApplyReplicatedBrick(brickIdx, ref reader);
                Assert.That(reader.Remaining, Is.EqualTo(0));

                Assert.That(dst.GetBlock(9, 10, 11), Is.EqualTo(new Block(0x8001)));
                Assert.That(dst.GetBlock(15, 8, 8), Is.EqualTo(new Block(0x8002)));
                Assert.That(dst.GetBlock(8, 8, 8).isEmpty, Is.True);
                Assert.That(dst.slots[2].IsCreated, Is.False, "non-replicated slot must not arrive");
                Assert.That(dst.NonEmptyBrickCount, Is.EqualTo(1));

                ushort dirty = dst.brickDirtyFlags[brickIdx];
                Assert.That(dirty & (ushort)DirtyFlags.Geometry, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)DirtyFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)DirtyFlags.BlockBrickAdded, Is.Not.EqualTo(0));
                Assert.That(dirty & (ushort)DirtyFlags.GeneralAutomata, Is.EqualTo(0), "replica must not trigger automata");
            }
            finally
            {
                source.Dispose(Allocator.Persistent);
                target.Dispose(Allocator.Persistent);
            }
        }

        [Test]
        public void ReplicatedBrick_SecondApplyOverwritesWithoutBrickAdded()
        {
            SectorHandle source = SectorHandle.AllocEmpty();
            SectorHandle target = SectorHandle.AllocEmpty();
            try
            {
                ref Sector src = ref source.Get();
                ref Sector dst = ref target.Get();
                int brickIdx = Sector.ToBrickIdx(0, 0, 0);

                src.SetBlock(1, 1, 1, new Block(0x8001));
                byte[] buffer = new byte[src.ReplicatedBrickBytes(Sector.DefaultReplicatedSlotMask)];
                int written;
                fixed (byte* dstPtr = buffer)
                {
                    var writer = new UnsafeNetWriter(dstPtr, buffer.Length);
                    src.WriteReplicatedBrick(ref writer, brickIdx, Sector.DefaultReplicatedSlotMask);
                    written = writer.Length;
                }

                var reader = new NetMessageReader(buffer, written);
                dst.ApplyReplicatedBrick(brickIdx, ref reader);
                dst.ClearAllDirtyFlags();

                src.SetBlock(1, 1, 1, Block.Empty);
                src.SetBlock(2, 2, 2, new Block(0x8003));
                fixed (byte* dstPtr = buffer)
                {
                    var writer = new UnsafeNetWriter(dstPtr, buffer.Length);
                    src.WriteReplicatedBrick(ref writer, brickIdx, Sector.DefaultReplicatedSlotMask);
                    written = writer.Length;
                }

                reader = new NetMessageReader(buffer, written);
                dst.ApplyReplicatedBrick(brickIdx, ref reader);

                Assert.That(dst.GetBlock(1, 1, 1).isEmpty, Is.True);
                Assert.That(dst.GetBlock(2, 2, 2), Is.EqualTo(new Block(0x8003)));
                Assert.That(dst.brickDirtyFlags[brickIdx] & (ushort)DirtyFlags.BlockBrickAdded, Is.EqualTo(0));
                Assert.That(dst.brickDirtyFlags[brickIdx] & (ushort)DirtyFlags.Geometry, Is.Not.EqualTo(0));
            }
            finally
            {
                source.Dispose(Allocator.Persistent);
                target.Dispose(Allocator.Persistent);
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

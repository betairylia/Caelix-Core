using NUnit.Framework;
using Unity.Collections;
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

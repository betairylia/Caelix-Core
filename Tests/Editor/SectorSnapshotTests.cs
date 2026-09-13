using NUnit.Framework;
using Unity.Collections;
using Caelix;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    public class SectorSnapshotTests
    {
        [Test]
        public unsafe void RepeatedSnapshotsReuseBothBuffersAndKeepUnchangedAuxData()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));
            scope.Sector.EnsureAuxAllocated(SectorSlotId.Block, BrickBitmask.Bytes);
            scope.Sector.slots[(int)SectorSlotId.Block].aux.Ptr[0] = 123;
            scope.Handle.ActivateSnapshot();
            scope.Handle.ApplySnapshot();

            var front = (System.IntPtr)scope.Sector.slots[(int)SectorSlotId.Block].data.Ptr;
            var back = (System.IntPtr)scope.Sector._snapshot_slots[(int)SectorSlotId.Block].data.Ptr;
            var auxFront = (System.IntPtr)scope.Sector.slots[(int)SectorSlotId.Block].aux.Ptr;
            var auxBack = (System.IntPtr)scope.Sector._snapshot_slots[(int)SectorSlotId.Block].aux.Ptr;
            for (ushort value = 2; value < 12; value++)
            {
                scope.Handle.ActivateSnapshot();
                Assert.That((System.IntPtr)scope.Sector._snapshot_slots[(int)SectorSlotId.Block].data.Ptr, Is.EqualTo(back));
                Assert.That((System.IntPtr)scope.Sector._snapshot_slots[(int)SectorSlotId.Block].aux.Ptr, Is.EqualTo(auxBack));
                scope.Handle.SetBlock(0, 0, 0, new Block(value));
                Assert.That(scope.Handle.GetBlock(0, 0, 0).id, Is.EqualTo(value - 1));
                scope.Handle.ApplySnapshot();
                Assert.That(scope.Sector.slots[(int)SectorSlotId.Block].aux.Ptr[0], Is.EqualTo(123));
                (front, back) = (back, front);
                (auxFront, auxBack) = (auxBack, auxFront);
            }
        }

        [Test]
        public unsafe void ReusedSnapshotCopiesNewSlotsAndDropsRemovedAux()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));
            scope.Sector.EnsureAuxAllocated(SectorSlotId.Block, BrickBitmask.Bytes);
            scope.Handle.ActivateSnapshot();
            scope.Handle.ApplySnapshot();
            scope.Sector.slots[(int)SectorSlotId.Block].aux.Dispose();
            scope.Sector.slots[(int)SectorSlotId.Block].aux = default;
            scope.Handle.SetSlot(SectorSlotId.Reserved1, 0, 0, 0, (ushort)42);
            scope.Handle.ActivateSnapshot();
            scope.Handle.ApplySnapshot();
            Assert.That(scope.Sector.slots[(int)SectorSlotId.Block].HasAux, Is.False);
            Assert.That(scope.Handle.GetSlot<ushort>(SectorSlotId.Reserved1, 0, 0, 0), Is.EqualTo(42));
            scope.Handle.SetBlock(127, 127, 127, new Block(9));
            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(2));
            scope.Handle.ApplySnapshot();
            Assert.That(scope.Handle.GetBlock(127, 127, 127).id, Is.EqualTo(9));
        }

        [Test]
        public void WritesAfterActivateSnapshotAreInvisibleUntilApply()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(2));

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(1)));

            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(2)));
        }

        [Test]
        public void SnapshotCanCreateANewBrickBeforeApply()
        {
            using var scope = new SectorTestScope();

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(127, 127, 127, new Block(3));

            Assert.That(scope.Handle.GetBlock(127, 127, 127).isEmpty, Is.True);

            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(127, 127, 127), Is.EqualTo(new Block(3)));
        }

        [Test]
        public void RepeatedActivateSnapshotBeforeApplyIsIdempotent()
        {
            using var scope = new SectorTestScope();
            scope.Handle.SetBlock(0, 0, 0, new Block(1));

            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(2));
            scope.Handle.ActivateSnapshot();
            scope.Handle.SetBlock(0, 0, 0, new Block(3));
            scope.Handle.ApplySnapshot();

            Assert.That(scope.Handle.GetBlock(0, 0, 0), Is.EqualTo(new Block(3)));
        }

        [Test]
        public void DisposingSectorWithActiveSnapshotIsSafe()
        {
            var handle = SectorHandle.AllocEmpty();
            handle.ActivateSnapshot(Allocator.Persistent);
            handle.SetBlock(0, 0, 0, new Block(4));

            Assert.DoesNotThrow(() => handle.Dispose(Allocator.Persistent));
            Assert.That(handle.IsNull, Is.True);
        }
    }
}

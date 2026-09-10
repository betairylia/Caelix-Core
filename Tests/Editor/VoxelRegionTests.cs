using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    /// <summary>
    /// <see cref="VoxelRegion"/> and the region API on <see cref="VoxelEntityData"/>: residency,
    /// detached generation and attachment, in-place writes, removal and bounds.
    /// </summary>
    public unsafe class VoxelRegionTests
    {
        private static Block Solid(int id = 1) => new Block((ushort)id);

        // ---- 1. Constants and conversions ---------------------------------------------------

        [Test]
        public void RegionConstantsMatchTheStorageBackend()
        {
            Assert.That(VoxelRegion.Shift, Is.EqualTo(BrickKey.Shift + Sector.SHIFT_IN_BRICKS));
            Assert.That(VoxelRegion.SizeInBlocks, Is.EqualTo(Sector.SECTOR_SIZE_IN_BLOCKS));
            Assert.That(VoxelRegion.SizeInBricks, Is.EqualTo(Sector.SIZE_IN_BRICKS));
        }

        [TestCase(0, 0, 0)]
        [TestCase(1, 0, 0)]
        [TestCase(-1, 2, -3)]
        public void RegionConversionsRoundTrip(int x, int y, int z)
        {
            var regionPos = new int3(x, y, z);

            int3 origin = VoxelRegion.OriginOf(regionPos);
            Assert.That(VoxelRegion.Of(origin), Is.EqualTo(regionPos));
            Assert.That(VoxelRegion.Of(origin + (VoxelRegion.SizeInBlocks - 1)), Is.EqualTo(regionPos));

            int3 first = VoxelRegion.FirstKeyOf(regionPos);
            int3 last = VoxelRegion.LastKeyOf(regionPos);
            Assert.That(VoxelRegion.OfKey(first), Is.EqualTo(regionPos));
            Assert.That(VoxelRegion.OfKey(last), Is.EqualTo(regionPos));
            Assert.That(last - first, Is.EqualTo(new int3(VoxelRegion.SizeInBricks - 1)));
            Assert.That(BrickKey.ToBlockOrigin(first), Is.EqualTo(origin));
        }

        // ---- 2. Create, fill, attach ---------------------------------------------------------

        [Test]
        public void DetachedRegionIsFilledThenAttached()
        {
            using var scope = new EntityDataTestScope();
            var regionPos = new int3(1, 0, 0);
            VoxelRegion region = VoxelRegion.Create(regionPos);
            bool owned = true;
            try
            {
                int3 origin = region.Origin;
                Assert.That(origin, Is.EqualTo(new int3(VoxelRegion.SizeInBlocks, 0, 0)));
                Assert.That(region.IsCreated, Is.True);
                Assert.That(region.IsAttached, Is.False);
                Assert.That(region.AllocatedBrickCount, Is.EqualTo(0));

                int3 near = origin + new int3(3, 3, 3);
                int3 far = origin + new int3(127, 127, 127);
                region.SetBlock(near, Solid(5));
                region.SetBlock(far, Solid(6));

                Assert.That(region.GetBlock(near), Is.EqualTo(Solid(5)));
                Assert.That(region.GetBlock(far), Is.EqualTo(Solid(6)));
                Assert.That(region.AllocatedBrickCount, Is.EqualTo(2));

                scope.Data.AttachRegion(ref region);
                owned = false;

                Assert.That(region.IsAttached, Is.True);
                Assert.That(scope.Data.RegionCount, Is.EqualTo(1));
                Assert.That(scope.Data.AllocatedBrickCount, Is.EqualTo(2));
                Assert.That(scope.Data.HasRegion(regionPos), Is.True);
                Assert.That(scope.Data.GetBlock(near), Is.EqualTo(Solid(5)));
                Assert.That(scope.Data.GetBlock(far), Is.EqualTo(Solid(6)));
                Assert.That(scope.Data.IsBrickAllocated(BrickKey.FromBlock(near)), Is.True);
                Assert.That(scope.Data.IsBrickAllocated(BrickKey.FromBlock(far)), Is.True);
            }
            finally
            {
                if (owned) region.Dispose();
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void DisposingAnAttachedRegionThrows()
        {
            using var scope = new EntityDataTestScope();
            VoxelRegion region = VoxelRegion.Create(new int3(0, 0, 0));
            bool owned = true;
            try
            {
                scope.Data.AttachRegion(ref region);
                owned = false;
                Assert.Throws<InvalidOperationException>(() => region.Dispose());
            }
            finally
            {
                if (owned) region.Dispose();
            }
        }

        [Test]
        public void RegionAccessOutsideItsBoxThrows()
        {
            VoxelRegion region = VoxelRegion.Create(new int3(0, 0, 0));
            try
            {
                Assert.Throws<IndexOutOfRangeException>(
                    () => region.SetBlock(new int3(VoxelRegion.SizeInBlocks, 0, 0), Solid()));
                Assert.Throws<IndexOutOfRangeException>(() => region.GetBlock(new int3(-1, 0, 0)));
            }
            finally
            {
                region.Dispose();
            }
        }

        [Test]
        public void CreateRejectsANonPersistentAllocator()
        {
            Assert.Throws<ArgumentException>(
                () => VoxelRegion.Create(new int3(0, 0, 0), 1, Allocator.Temp));
        }
#endif

        [Test]
        public void AttachRegionMarksEveryBrickItBrought()
        {
            using var scope = new EntityDataTestScope();
            VoxelRegion region = VoxelRegion.Create(new int3(0, 0, 0));
            bool owned = true;
            try
            {
                region.SetBlock(new int3(1, 1, 1), Solid());     // key (0,0,0)
                region.SetBlock(new int3(70, 1, 1), Solid());    // key (8,0,0)

                scope.Data.AttachRegion(ref region);
                owned = false;

                // Replication derives its delta from the change list, so an attached region must
                // announce the bricks it brought; nothing else ever marks them.
                scope.Data.BuildChangeList();
                foreach (int3 key in new[] { new int3(0, 0, 0), new int3(8, 0, 0) })
                {
                    BrickChange change = SingleChange(scope.Data, key);
                    Assert.That(change.Kind, Is.EqualTo(ChangeKind.Updated));
                    Assert.That(
                        change.SourceFlags & DirtyFlags.BlockBrickAdded,
                        Is.EqualTo(DirtyFlags.BlockBrickAdded));
                }
            }
            finally
            {
                if (owned) region.Dispose();
            }
        }

        [Test]
        public void AttachRegionRejectsADuplicateAnUnbuiltAndAnAttachedRegion()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.EnsureRegion(new int3(0, 0, 0));

            VoxelRegion duplicate = VoxelRegion.Create(new int3(0, 0, 0));
            try
            {
                Assert.Throws<InvalidOperationException>(() => scope.Data.AttachRegion(ref duplicate));
                Assert.That(duplicate.IsAttached, Is.False);
            }
            finally
            {
                duplicate.Dispose();
            }

            VoxelRegion empty = default;
            Assert.Throws<InvalidOperationException>(() => scope.Data.AttachRegion(ref empty));

            VoxelRegion attached = VoxelRegion.Create(new int3(4, 0, 0));
            bool owned = true;
            try
            {
                scope.Data.AttachRegion(ref attached);
                owned = false;
                Assert.Throws<InvalidOperationException>(() => scope.Data.AttachRegion(ref attached));
            }
            finally
            {
                if (owned) attached.Dispose();
            }
        }

        // ---- 3. In-place writes ----------------------------------------------------------------

        [Test]
        public void TryOpenRegionWritesInPlace()
        {
            using var scope = new EntityDataTestScope();
            var regionPos = new int3(2, 0, 0);
            scope.Data.EnsureRegion(regionPos);
            scope.Data.EnsureRegion(regionPos);   // idempotent
            Assert.That(scope.Data.RegionCount, Is.EqualTo(1));

            Assert.That(scope.Data.TryOpenRegion(regionPos, out VoxelRegion region), Is.True);
            Assert.That(region.IsAttached, Is.True);
            Assert.That(region.RegionPos, Is.EqualTo(regionPos));

            int3 pos = region.Origin + new int3(4, 5, 6);
            region.SetBlock(pos, Solid(9));
            Assert.That(scope.Data.GetBlock(pos), Is.EqualTo(Solid(9)));
            Assert.That(region.GetBlock(pos), Is.EqualTo(Solid(9)));
            Assert.That(region.AllocatedBrickCount, Is.EqualTo(1));

            Assert.That(scope.Data.TryOpenRegion(new int3(3, 0, 0), out VoxelRegion absent), Is.False);
            Assert.That(absent.IsCreated, Is.False);
        }

        // ---- 4. Removal ------------------------------------------------------------------------

        [Test]
        public void RemoveRegionFreesStorageAndListsItsBricks()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(0, 0, 0), Solid());     // key (0,0,0)
            scope.Data.SetBlock(new int3(64, 0, 0), Solid());    // key (8,0,0)
            scope.Data.EnsureRegion(new int3(1, 0, 0));          // a survivor for the boundary marking
            scope.Data.ClearChanges();

            Assert.That(scope.Data.RemoveRegion(int3.zero), Is.True);
            Assert.That(scope.Data.HasRegion(int3.zero), Is.False);
            Assert.That(scope.Data.RegionCount, Is.EqualTo(1));
            Assert.That(scope.Data.AllocatedBrickCount, Is.EqualTo(0));

            var removed = new List<int3>();
            for (int i = 0; i < scope.Data.ChangeCount; i++)
            {
                BrickChange change = scope.Data.GetChange(i);
                Assert.That(change.Kind, Is.EqualTo(ChangeKind.Removed));
                removed.Add(change.Key);
            }

            Assert.That(removed.Count, Is.EqualTo(2));
            Assert.That(removed, Does.Contain(new int3(0, 0, 0)));
            Assert.That(removed, Does.Contain(new int3(8, 0, 0)));

            Assert.That(scope.Data.RemoveRegion(int3.zero), Is.False);
        }

        // ---- 5. Bounds and positions -------------------------------------------------------------

        [Test]
        public void TryGetStorageBoundsUnionsTheRegionBoxes()
        {
            using var scope = new EntityDataTestScope();
            Assert.That(scope.Data.TryGetStorageBounds(out int3 emptyMin, out int3 emptyMax), Is.False);
            Assert.That(emptyMin, Is.EqualTo(int3.zero));
            Assert.That(emptyMax, Is.EqualTo(int3.zero));

            scope.Data.EnsureRegion(new int3(1, 0, 0));
            scope.Data.EnsureRegion(new int3(0, 2, 0));

            Assert.That(scope.Data.TryGetStorageBounds(out int3 min, out int3 maxExclusive), Is.True);
            Assert.That(min, Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(maxExclusive, Is.EqualTo(new int3(256, 384, 128)));
        }

        [Test]
        public void GetRegionPositionsListsEveryRegion()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.EnsureRegion(new int3(1, 0, 0));
            scope.Data.EnsureRegion(new int3(0, 2, 0));

            NativeArray<int3> positions = scope.Data.GetRegionPositions(Allocator.Temp);
            try
            {
                Assert.That(positions.Length, Is.EqualTo(2));
                var seen = new List<int3>();
                for (int i = 0; i < positions.Length; i++) seen.Add(positions[i]);
                Assert.That(seen, Does.Contain(new int3(1, 0, 0)));
                Assert.That(seen, Does.Contain(new int3(0, 2, 0)));
            }
            finally
            {
                positions.Dispose();
            }
        }

        private static BrickChange SingleChange(VoxelEntityData data, int3 key)
        {
            var found = new List<BrickChange>();
            for (int i = 0; i < data.ChangeCount; i++)
            {
                BrickChange change = data.GetChange(i);
                if (math.all(change.Key == key))
                {
                    found.Add(change);
                }
            }

            Assert.That(found.Count, Is.EqualTo(1), $"expected exactly one change for brick {key}");
            return found[0];
        }
    }
}

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
    /// The brick-key facade on <see cref="VoxelEntityData"/>: keys, bulk binds, enumeration, the
    /// neighbourhood window, the per-cycle change list and the storage epoch.
    /// </summary>
    public unsafe class BrickFacadeTests
    {
        private static Block Solid(int id = 1) => new Block((ushort)id);

        // ---- 1. Keys -----------------------------------------------------------------------

        [Test]
        public void BrickKeyShiftMatchesTheSectorBackend()
        {
            Assert.That(BrickKey.Shift, Is.EqualTo(Sector.SHIFT_IN_BLOCKS));
            Assert.That(BrickKey.Mask, Is.EqualTo(Sector.BRICK_MASK));
            Assert.That(BrickKey.BlocksPerAxis, Is.EqualTo(Sector.SIZE_IN_BLOCKS));
            Assert.That(BrickKey.BlocksInBrick, Is.EqualTo(Sector.BLOCKS_IN_BRICK));
        }

        [Test]
        public void BrickKeyBlockIndexMatchesTheSectorBackend()
        {
            for (int z = 0; z < BrickKey.BlocksPerAxis; z++)
            for (int y = 0; y < BrickKey.BlocksPerAxis; y++)
            for (int x = 0; x < BrickKey.BlocksPerAxis; x++)
            {
                Assert.That(BrickKey.ToBlockIdx(x, y, z), Is.EqualTo(Sector.ToBlockIdx(x, y, z)));
            }
        }

        [TestCase(0, 0, 0)]
        [TestCase(7, 7, 7)]
        [TestCase(8, 15, 16)]
        [TestCase(-1, -1, -1)]
        [TestCase(-8, -9, -16)]
        [TestCase(-129, 130, -131)]
        public void BrickKeyRoundTripsBlockPositions(int x, int y, int z)
        {
            var blockPos = new int3(x, y, z);

            int3 key = BrickKey.FromBlock(blockPos);
            int3 origin = BrickKey.ToBlockOrigin(key);
            int3 local = BrickKey.LocalBlock(blockPos);

            Assert.That(math.all(local >= 0) && math.all(local < BrickKey.BlocksPerAxis), Is.True);
            Assert.That(origin + local, Is.EqualTo(blockPos));
            Assert.That(BrickKey.FromBlock(origin), Is.EqualTo(key));
        }

        [Test]
        public void SectorKeysRoundTripBrickKeys()
        {
            var keys = new[]
            {
                new int3(0, 0, 0), new int3(15, 15, 15), new int3(16, 0, 0),
                new int3(-1, -1, -1), new int3(-16, -17, 33)
            };

            foreach (int3 key in keys)
            {
                int3 sectorPos = SectorKeys.ToSectorPos(key);
                int brickIdx = SectorKeys.ToBrickIdx(key);
                Assert.That(SectorKeys.FromSector(sectorPos, brickIdx), Is.EqualTo(key));
            }
        }

        // ---- 2. Binding --------------------------------------------------------------------

        [Test]
        public void TryBindBrickReturnsTheBackendPointer()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(3, 4, 5), Solid());

            var key = new int3(0, 0, 0);
            ref Sector sector = ref scope.SectorAt(int3.zero).Get();
            short bid = sector.brickIdx[Sector.ToBrickIdx(0, 0, 0)];
            Block* expected = sector.GetBrick<Block>(SectorSlotId.Block, bid);

            Assert.That(scope.Data.IsBrickAllocated(key), Is.True);
            Assert.That(scope.Data.TryBindBrick(SectorSlotId.Block, key, out Block* actual), Is.True);
            Assert.That((IntPtr)actual, Is.EqualTo((IntPtr)expected));
            Assert.That(actual[Sector.ToBlockIdx(3, 4, 5)], Is.EqualTo(Solid()));
        }

        [Test]
        public void TryBindBrickFailsForUnallocatedBrickMissingSectorAndUncreatedSlot()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(3, 4, 5), Solid());

            // Unallocated brick inside an existing sector.
            Assert.That(scope.Data.IsBrickAllocated(new int3(5, 5, 5)), Is.False);
            Assert.That(scope.Data.TryBindBrick(SectorSlotId.Block, new int3(5, 5, 5), out Block* a), Is.False);
            Assert.That((IntPtr)a, Is.EqualTo(IntPtr.Zero));

            // Missing sector.
            Assert.That(scope.Data.TryBindBrick(SectorSlotId.Block, new int3(64, 0, 0), out Block* b), Is.False);
            Assert.That((IntPtr)b, Is.EqualTo(IntPtr.Zero));

            // Allocated brick, slot never created.
            Assert.That(scope.Data.TryBindBrick(SectorSlotId.Reserved1, new int3(0, 0, 0), out Block* c), Is.False);
            Assert.That((IntPtr)c, Is.EqualTo(IntPtr.Zero));
            Assert.That(scope.Data.TryBindBrickAux(SectorSlotId.Block, new int3(0, 0, 0), out void* aux), Is.False);
            Assert.That((IntPtr)aux, Is.EqualTo(IntPtr.Zero));
        }

        // ---- 3. Enumeration ----------------------------------------------------------------

        [Test]
        public void EnumerateBricksVisitsAllocatedBricksInsideTheRange()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(0, 0, 0), Solid());       // key (0,0,0),  sector (0,0,0)
            scope.Data.SetBlock(new int3(64, 0, 0), Solid());      // key (8,0,0),  sector (0,0,0)
            scope.Data.SetBlock(new int3(128, 0, 0), Solid());     // key (16,0,0), sector (1,0,0)

            List<int3> all = Collect(scope.Data.EnumerateBricks());
            Assert.That(all.Count, Is.EqualTo(3));
            Assert.That(all, Does.Contain(new int3(0, 0, 0)));
            Assert.That(all, Does.Contain(new int3(8, 0, 0)));
            Assert.That(all, Does.Contain(new int3(16, 0, 0)));

            List<int3> firstSector = Collect(
                scope.Data.EnumerateBricks(new int3(0, 0, 0), new int3(15, 15, 15)));
            Assert.That(firstSector.Count, Is.EqualTo(2));
            Assert.That(firstSector, Does.Contain(new int3(0, 0, 0)));
            Assert.That(firstSector, Does.Contain(new int3(8, 0, 0)));

            List<int3> single = Collect(
                scope.Data.EnumerateBricks(new int3(16, 0, 0), new int3(16, 0, 0)));
            Assert.That(single, Is.EqualTo(new List<int3> { new int3(16, 0, 0) }));

            List<int3> none = Collect(
                scope.Data.EnumerateBricks(new int3(100, 100, 100), new int3(110, 110, 110)));
            Assert.That(none, Is.Empty);
        }

        private static List<int3> Collect(BrickKeyEnumerator enumerator)
        {
            var keys = new List<int3>();
            foreach (int3 key in enumerator)
            {
                keys.Add(key);
            }

            return keys;
        }

        // ---- 4. Neighbourhood --------------------------------------------------------------

        [Test]
        public void OpenNeighborhoodReadsAcrossBrickAndSectorBorders()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(7, 0, 0), Solid(11));      // brick (0,0,0)
            scope.Data.SetBlock(new int3(8, 0, 0), Solid(12));      // brick (1,0,0)
            scope.Data.SetBlock(new int3(127, 5, 5), Solid(13));    // brick (15,0,0), sector (0,0,0)
            scope.Data.SetBlock(new int3(128, 5, 5), Solid(14));    // brick (16,0,0), sector (1,0,0)

            VoxelNeighborhood atOrigin = scope.Data.OpenNeighborhood(new int3(0, 0, 0));
            Assert.That(atOrigin.GetBlock(new int3(7, 0, 0)), Is.EqualTo(Solid(11)));
            Assert.That(atOrigin.GetBlock(new int3(8, 0, 0)), Is.EqualTo(Solid(12)));
            Assert.That(atOrigin.GetBlock(new int3(1, 0, 0)), Is.EqualTo(Block.Empty));
            Assert.That((IntPtr)atOrigin.Center, Is.Not.EqualTo(IntPtr.Zero));

            VoxelNeighborhood atSectorEdge = scope.Data.OpenNeighborhood(new int3(15, 0, 0));
            Assert.That(atSectorEdge.GetBlock(new int3(127, 5, 5)), Is.EqualTo(Solid(13)));
            Assert.That(atSectorEdge.GetBlock(new int3(128, 5, 5)), Is.EqualTo(Solid(14)));
        }

        [Test]
        public void OpenNeighborhoodReadsDefaultWhereTheSectorIsAbsent()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(0, 0, 0), Solid());

            VoxelNeighborhood window = scope.Data.OpenNeighborhood(new int3(16, 0, 0));

            Assert.That((IntPtr)window.Center, Is.EqualTo(IntPtr.Zero));
            Assert.That(window.GetBlock(new int3(128, 0, 0)), Is.EqualTo(Block.Empty));
            Assert.That(window.GetBlock(new int3(127, 0, 0)), Is.EqualTo(Block.Empty));
        }

        // ---- 5. Change list, boundary edit --------------------------------------------------

        [Test]
        public void BoundaryEditListsTheSourceBrickAndItsNeighbour()
        {
            using var scope = new EntityDataTestScope();

            // The change list only names ALLOCATED bricks, so the neighbour has to hold storage
            // before it can be listed. Round one allocates it and its dirtiness is then cleared.
            scope.Data.SetBlock(new int3(12, 3, 3), Solid());
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            // Round two: one voxel at the +X face of brick A.
            scope.Data.SetBlock(new int3(7, 3, 3), Solid());
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.BuildChangeList();

            BrickChange source = SingleChange(scope.Data, new int3(0, 0, 0));
            Assert.That(source.Kind, Is.EqualTo(ChangeKind.Updated));
            Assert.That(source.SourceFlags & DirtyFlags.Geometry, Is.EqualTo(DirtyFlags.Geometry));
            Assert.That(
                source.RequiredFlags & DirtyFlags.GeometryWithLocalNeighbor,
                Is.EqualTo(DirtyFlags.GeometryWithLocalNeighbor));

            BrickChange neighbour = SingleChange(scope.Data, new int3(1, 0, 0));
            Assert.That(neighbour.Kind, Is.EqualTo(ChangeKind.Updated));
            Assert.That(neighbour.SourceFlags, Is.EqualTo(DirtyFlags.None));
            Assert.That(
                neighbour.RequiredFlags & DirtyFlags.GeometryWithLocalNeighbor,
                Is.EqualTo(DirtyFlags.GeometryWithLocalNeighbor));

            scope.Data.ClearChanges();
            Assert.That(scope.Data.ChangeCount, Is.EqualTo(0));

            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.BuildChangeList();
            Assert.That(scope.Data.ChangeCount, Is.EqualTo(0));
        }

        [Test]
        public void ChangesViewMatchesTheIndexedAccessor()
        {
            using var scope = new EntityDataTestScope();
            Assert.That(scope.Data.Changes.Length, Is.EqualTo(0), "an empty change list reads as an empty view");

            scope.Data.SetBlock(new int3(1, 1, 1), Solid());
            scope.Data.SetBlock(new int3(200, 200, 200), Solid());
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.BuildChangeList();

            NativeArray<BrickChange>.ReadOnly changes = scope.Data.Changes;
            Assert.That(changes.Length, Is.EqualTo(scope.Data.ChangeCount));
            for (int i = 0; i < changes.Length; i++)
            {
                Assert.That(changes[i].Key, Is.EqualTo(scope.Data.GetChange(i).Key));
                Assert.That(changes[i].Kind, Is.EqualTo(scope.Data.GetChange(i).Kind));
                Assert.That(changes[i].SourceFlags, Is.EqualTo(scope.Data.GetChange(i).SourceFlags));
                Assert.That(changes[i].RequiredFlags, Is.EqualTo(scope.Data.GetChange(i).RequiredFlags));
            }

            scope.Data.ClearChanges();
            Assert.That(scope.Data.Changes.Length, Is.EqualTo(0));
        }

        // ---- 6. Removal ---------------------------------------------------------------------

        [Test]
        public void SectorRemovalPublishesOneRemovedEntryPerAllocatedBrick()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(0, 0, 0), Solid());     // key (0,0,0)
            scope.Data.SetBlock(new int3(64, 0, 0), Solid());    // key (8,0,0)
            scope.Data.AddEmptySectorAt(new int3(1, 0, 0));      // a survivor for the boundary marking
            scope.Data.ClearChanges();

            Assert.That(scope.Data.RemoveSectorAt(int3.zero), Is.True);

            Assert.That(scope.Data.ChangeCount, Is.EqualTo(2));
            var removed = new List<int3>();
            for (int i = 0; i < scope.Data.ChangeCount; i++)
            {
                BrickChange change = scope.Data.GetChange(i);
                Assert.That(change.Kind, Is.EqualTo(ChangeKind.Removed));
                Assert.That(
                    change.RequiredFlags & DirtyFlags.BlockBrickRemoved,
                    Is.EqualTo(DirtyFlags.BlockBrickRemoved));
                removed.Add(change.Key);
            }

            Assert.That(removed, Does.Contain(new int3(0, 0, 0)));
            Assert.That(removed, Does.Contain(new int3(8, 0, 0)));

            scope.Data.ClearChanges();
            Assert.That(scope.Data.ChangeCount, Is.EqualTo(0));
        }

        // ---- 7. Two edit rounds, one build ---------------------------------------------------

        [Test]
        public void TwoEditRoundsBeforeOneBuildListEveryBrickExactlyOnce()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());          // brick (0,0,0)
            scope.Data.SetBlock(new int3(200, 200, 200), Solid());    // brick (25,25,25)
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();

            scope.Data.BuildChangeList();

            Assert.That(scope.Data.ChangeCount, Is.EqualTo(2));
            Assert.That(SingleChange(scope.Data, new int3(0, 0, 0)).Kind, Is.EqualTo(ChangeKind.Updated));
            Assert.That(SingleChange(scope.Data, new int3(25, 25, 25)).Kind, Is.EqualTo(ChangeKind.Updated));
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void SecondBuildWithoutClearThrows()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.BuildChangeList();

            EntityDataTestScope captured = scope;
            Assert.Throws<InvalidOperationException>(() => captured.Data.BuildChangeList());

            captured.Data.ClearChanges();
            Assert.DoesNotThrow(() => captured.Data.BuildChangeList());
        }
#endif

        // ---- 8. Required bricks ---------------------------------------------------------------

        [TestCase(true)]
        [TestCase(false)]
        public void CollectRequiredBricksMatchesTheSectorEnumerator(bool includeEmpty)
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(7, 7, 7), Solid());
            scope.Data.SetBlock(new int3(200, 200, 200), Solid());
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();

            var expected = new Dictionary<int3, ushort>();
            foreach (var sectorEntry in scope.Data.sectors)
            {
                ref Sector sector = ref sectorEntry.Value.Get();
                var enumerator = new SectorRequiredUpdateBrickEnumerator(sector, DirtyFlags.All, includeEmpty);
                foreach (BrickIterator brick in enumerator)
                {
                    int3 brickPos = brick.position >> BrickKey.Shift;
                    int brickIdxAbs = Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z);
                    expected[SectorKeys.FromSector(sectorEntry.Key, brickIdxAbs)] =
                        sector.brickRequireUpdateFlags[brickIdxAbs];
                }
            }

            using var collected = new NativeList<RequiredBrick>(Allocator.Persistent);
            scope.Data.CollectRequiredBricks(scope.Data.Guid, DirtyFlags.All, includeEmpty, collected);

            Assert.That(collected.Length, Is.EqualTo(expected.Count));
            for (int i = 0; i < collected.Length; i++)
            {
                RequiredBrick record = collected[i];
                Assert.That(expected.ContainsKey(record.Key), Is.True, $"unexpected key {record.Key}");
                Assert.That((ushort)record.Flags, Is.EqualTo(expected[record.Key]));
                Assert.That(record.Entity, Is.EqualTo(scope.Data.Guid));
                expected.Remove(record.Key);
            }

            Assert.That(expected, Is.Empty);
        }

        // ---- 9. Storage epoch ------------------------------------------------------------------

        [Test]
        public void StorageEpochAdvancesOnEveryStorageMutation()
        {
            using var scope = new EntityDataTestScope();

            uint start = scope.Data.StorageEpoch;
            scope.Data.AddEmptySectorAt(int3.zero);
            uint afterAdd = scope.Data.StorageEpoch;
            Assert.That(afterAdd, Is.GreaterThan(start));

            scope.Data.MarkStorageMutated();
            uint afterMark = scope.Data.StorageEpoch;
            Assert.That(afterMark, Is.GreaterThan(afterAdd));

            Assert.That(scope.Data.RemoveSectorAt(int3.zero), Is.True);
            Assert.That(scope.Data.StorageEpoch, Is.GreaterThan(afterMark));
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

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.IO;
using Caelix.Net;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    /// <summary>
    /// The additive brick-key facade members: the lookup cursor, flag markers and readers, the
    /// per-brick bitmask enumeration, derived-slot creation, the aux window, keyed voxel-slot
    /// writes and the keyed preview build.
    /// </summary>
    public unsafe class BrickFacadeCursorTests
    {
        private static Block Solid(int id = 1) => new Block((ushort)id);

        // ---- 1. Cursor ---------------------------------------------------------------------

        [Test]
        public void CursorFilledFromOneEntityDoesNotAnswerForAnother()
        {
            // Reviewer probe (milestone 3 review): A holds brick (0,0,0); B has an empty region at
            // the same coordinate. A cursor filled from A must not report A's storage for B.
            using var a = new EntityDataTestScope();
            using var b = new EntityDataTestScope();
            a.Data.SetBlock(new int3(1, 1, 1), Solid());
            b.Data.EnsureRegion(int3.zero);

            BrickCursor cursor = default;
            Assert.That(a.Data.IsBrickAllocated(int3.zero, ref cursor), Is.True);
            Assert.That(b.Data.IsBrickAllocated(int3.zero), Is.False);
            Assert.That(b.Data.IsBrickAllocated(int3.zero, ref cursor), Is.False, "cursor reused across entities");
            Assert.That(b.Data.TryBindBrick(SectorSlotId.Block, int3.zero, ref cursor, out Block* bound), Is.False);
            Assert.That((IntPtr)bound, Is.EqualTo(IntPtr.Zero));

            // And back: the cursor now belongs to B and must re-resolve for A.
            Assert.That(a.Data.IsBrickAllocated(int3.zero, ref cursor), Is.True);
        }

        [Test]
        public void CursorOverloadsMatchThePlainOnes()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(3, 4, 5), Solid());                             // key (0,0,0)
            scope.Data.SetBlock(new int3(VoxelRegion.SizeInBlocks + 3, 4, 5), Solid());  // key (16,0,0)
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.RefreshNonEmptyMask();

            int3[] keys =
            {
                new int3(0, 0, 0),      // allocated, region 0
                new int3(1, 0, 0),      // unallocated, region 0
                new int3(16, 0, 0),     // allocated, region 1
                new int3(17, 0, 0),     // unallocated, region 1
                new int3(160, 0, 0),    // missing region
                new int3(161, 0, 0),    // missing region again
                new int3(0, 0, 0)       // back to region 0
            };

            BrickCursor cursor = default;
            foreach (int3 key in keys)
            {
                Assert.That(
                    scope.Data.IsBrickAllocated(key, ref cursor),
                    Is.EqualTo(scope.Data.IsBrickAllocated(key)), $"IsBrickAllocated {key}");

                bool cursorBound = scope.Data.TryBindBrick(SectorSlotId.Block, key, ref cursor, out Block* withCursor);
                bool plainBound = scope.Data.TryBindBrick(SectorSlotId.Block, key, out Block* plain);
                Assert.That(cursorBound, Is.EqualTo(plainBound), $"TryBindBrick {key}");
                Assert.That((IntPtr)withCursor, Is.EqualTo((IntPtr)plain), $"TryBindBrick pointer {key}");

                bool cursorAux = scope.Data.TryBindBrickAux(SectorSlotId.Block, key, ref cursor, out void* auxWithCursor);
                bool plainAux = scope.Data.TryBindBrickAux(SectorSlotId.Block, key, out void* auxPlain);
                Assert.That(cursorAux, Is.EqualTo(plainAux), $"TryBindBrickAux {key}");
                Assert.That((IntPtr)auxWithCursor, Is.EqualTo((IntPtr)auxPlain), $"TryBindBrickAux pointer {key}");
            }
        }

        [Test]
        public void TheCursorCachesAMissingRegionToo()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(3, 4, 5), Solid());

            BrickCursor cursor = default;
            Assert.That(cursor.Valid, Is.False);

            Assert.That(scope.Data.IsBrickAllocated(new int3(160, 0, 0), ref cursor), Is.False);
            Assert.That(cursor.Valid, Is.True, "the miss itself is cached");
            Assert.That(cursor.Handle.IsNull, Is.True);
            Assert.That(cursor.SectorPos, Is.EqualTo(new int3(10, 0, 0)));

            // A second key in the same missing region reuses the cached miss.
            Assert.That(scope.Data.IsBrickAllocated(new int3(161, 0, 0), ref cursor), Is.False);
            Assert.That(cursor.Handle.IsNull, Is.True);
            Assert.That(cursor.SectorPos, Is.EqualTo(new int3(10, 0, 0)));

            // Crossing back into a live region refreshes the cache.
            Assert.That(scope.Data.IsBrickAllocated(new int3(0, 0, 0), ref cursor), Is.True);
            Assert.That(cursor.Handle.IsNull, Is.False);
            Assert.That(cursor.SectorPos, Is.EqualTo(new int3(0, 0, 0)));
        }

        // ---- 2. Flags ----------------------------------------------------------------------

        [Test]
        public void FlagMarkersAndReadersWorkOnKeys()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(7, 3, 3), Solid());    // key (0,0,0)
            scope.Data.SetBlock(new int3(8, 3, 3), Solid());    // key (1,0,0)
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            Assert.That(scope.Data.GetRequiredFlags(new int3(0, 0, 0)), Is.EqualTo(BrickUpdateFlags.None));

            scope.Data.MarkBrickDirty(new int3(0, 0, 0), BrickUpdateFlags.GeometryWithLocalNeighbor);
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();

            Assert.That(
                scope.Data.GetRequiredFlags(new int3(0, 0, 0)) & BrickUpdateFlags.GeometryWithLocalNeighbor,
                Is.EqualTo(BrickUpdateFlags.GeometryWithLocalNeighbor));
            Assert.That(
                scope.Data.GetRequiredFlags(new int3(1, 0, 0)) & BrickUpdateFlags.GeometryWithLocalNeighbor,
                Is.EqualTo(BrickUpdateFlags.GeometryWithLocalNeighbor),
                "the marker propagates to the neighbouring brick");

            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            Assert.That(scope.Data.MarkRequired(new int3(1, 0, 0), BrickUpdateFlags.GeneralAutomata), Is.True);
            Assert.That(
                scope.Data.MarkRequired(new int3(5, 5, 5), BrickUpdateFlags.GeneralAutomata), Is.False,
                "an unallocated brick has nothing to work on");
            Assert.That(
                scope.Data.MarkRequired(new int3(1000, 0, 0), BrickUpdateFlags.GeneralAutomata), Is.False,
                "a missing region has nothing to work on");
            Assert.That(
                scope.Data.GetRequiredFlags(new int3(1, 0, 0)) & BrickUpdateFlags.GeneralAutomata,
                Is.EqualTo(BrickUpdateFlags.GeneralAutomata));
            Assert.That(scope.Data.GetRequiredFlags(new int3(1000, 0, 0)), Is.EqualTo(BrickUpdateFlags.None));

            // A marker on a missing region is a no-op, and creates nothing.
            scope.Data.MarkBrickDirty(new int3(1000, 0, 0), BrickUpdateFlags.Geometry);
            Assert.That(scope.Data.HasRegion(new int3(7, 0, 0)), Is.False);
        }

        [Test]
        public void EnumerateBrickDirtyFlagsReadsTheDirtySide()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());                                          // key (0,0,0)
            scope.Data.SetBlock(VoxelRegion.OriginOf(new int3(1, 0, 0)) + new int3(1, 1, 1), Solid()); // key (16,0,0)

            Dictionary<int3, BrickUpdateFlags> dirty = CollectDirtyFlags(scope.Data, BrickUpdateFlags.All, true);
            Assert.That(dirty.Count, Is.EqualTo(2));
            Assert.That(dirty.ContainsKey(new int3(0, 0, 0)), Is.True);
            Assert.That(dirty.ContainsKey(new int3(16, 0, 0)), Is.True);
            foreach (KeyValuePair<int3, BrickUpdateFlags> entry in dirty)
            {
                Assert.That(entry.Value & BrickUpdateFlags.Geometry, Is.EqualTo(BrickUpdateFlags.Geometry));
            }

            Dictionary<int3, BrickUpdateFlags> everything = CollectDirtyFlags(scope.Data, BrickUpdateFlags.All, false);
            Assert.That(everything.Count, Is.EqualTo(2));

            // A narrower mask keeps only the flags asked for.
            Dictionary<int3, BrickUpdateFlags> narrow = CollectDirtyFlags(scope.Data, BrickUpdateFlags.Geometry, true);
            Assert.That(narrow.Count, Is.EqualTo(2));
            foreach (KeyValuePair<int3, BrickUpdateFlags> entry in narrow)
            {
                Assert.That(entry.Value, Is.EqualTo(BrickUpdateFlags.Geometry));
            }

            scope.Data.ClearDirtyFlags();
            Assert.That(CollectDirtyFlags(scope.Data, BrickUpdateFlags.All, true).Count, Is.EqualTo(0));

            Dictionary<int3, BrickUpdateFlags> afterClear = CollectDirtyFlags(scope.Data, BrickUpdateFlags.All, false);
            Assert.That(afterClear.Count, Is.EqualTo(2), "the full walk still lists every allocated brick");
            foreach (KeyValuePair<int3, BrickUpdateFlags> entry in afterClear)
            {
                Assert.That(entry.Value, Is.EqualTo(BrickUpdateFlags.None));
            }
        }

        // ---- 3. Per-brick bitmask ------------------------------------------------------------

        [Test]
        public void EnumerateBrickBitmaskYieldsTheWrittenVoxels()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 2, 3), Solid(4));
            scope.Data.SetBlock(new int3(5, 6, 7), Solid(8));
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.RefreshNonEmptyMask();

            var found = new List<int3>();
            foreach (BrickSlotEntry<Block> item in
                     scope.Data.EnumerateBrickBitmask<Block>(SectorSlotId.Block, new int3(0, 0, 0)))
            {
                found.Add(item.position);
            }

            Assert.That(found.Count, Is.EqualTo(2));
            Assert.That(found, Does.Contain(new int3(1, 2, 3)));
            Assert.That(found, Does.Contain(new int3(5, 6, 7)));

            int empty = 0;
            foreach (BrickSlotEntry<Block> _ in
                     scope.Data.EnumerateBrickBitmask<Block>(SectorSlotId.Block, new int3(9, 9, 9)))
            {
                empty++;
            }

            Assert.That(empty, Is.EqualTo(0), "an unallocated brick yields nothing rather than throwing");
        }

        // ---- 4. Derived slots ------------------------------------------------------------------

        [Test]
        public void EnsureSlotCreatesTheSlotOnceAndReportsTheCoveredBricks()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());                                          // key (0,0,0)
            scope.Data.SetBlock(new int3(70, 1, 1), Solid());                                         // key (8,0,0)
            scope.Data.SetBlock(VoxelRegion.OriginOf(new int3(1, 0, 0)) + new int3(1, 1, 1), Solid()); // key (16,0,0)

            using var covered = new NativeList<int3>(Allocator.Persistent);
            scope.Data.EnsureSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo, BrickBitmask.Bytes, covered);

            var keys = new List<int3>();
            for (int i = 0; i < covered.Length; i++) keys.Add(covered[i]);
            Assert.That(keys.Count, Is.EqualTo(3));
            Assert.That(keys, Does.Contain(new int3(0, 0, 0)));
            Assert.That(keys, Does.Contain(new int3(8, 0, 0)));
            Assert.That(keys, Does.Contain(new int3(16, 0, 0)));

            Assert.That(
                scope.Data.TryBindBrick(SectorSlotId.PhysicsInfo, new int3(0, 0, 0), out PhysicsInfo* data),
                Is.True);
            Assert.That((IntPtr)data, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(
                scope.Data.TryBindBrickAux(SectorSlotId.PhysicsInfo, new int3(0, 0, 0), out void* aux),
                Is.True);
            Assert.That((IntPtr)aux, Is.Not.EqualTo(IntPtr.Zero));

            covered.Clear();
            scope.Data.EnsureSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo, BrickBitmask.Bytes, covered);
            Assert.That(covered.Length, Is.EqualTo(0), "a second call has nothing to rebuild");
        }

        [Test]
        public void EnsureSlotRefusesTheBlockSlot()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());

            VoxelEntityData data = scope.Data;
            using var covered = new NativeList<int3>(Allocator.Persistent);
            NativeList<int3> captured = covered;
            Assert.Throws<ArgumentException>(
                () => data.EnsureSlot<Block>(SectorSlotId.Block, 0, captured));
        }

        // ---- 5. Aux window ------------------------------------------------------------------------

        [Test]
        public void OpenNeighborhoodBindsTheAuxSliceOfEachBrick()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.RefreshNonEmptyMask();

            int center = VoxelNeighborhood.Index(0, 0, 0);
            int neighbour = VoxelNeighborhood.Index(1, 0, 0);
            Assert.That(center, Is.EqualTo(13));

            VoxelNeighborhood plain = scope.Data.OpenNeighborhood(new int3(0, 0, 0));
            Assert.That(
                (IntPtr)plain.GetAuxPtr(center), Is.EqualTo(IntPtr.Zero),
                "the one-argument overload binds no aux");
            Assert.That((IntPtr)plain.GetBrickPtr(center), Is.EqualTo((IntPtr)plain.Center));

            VoxelNeighborhood window = scope.Data.OpenNeighborhood(new int3(0, 0, 0), SectorSlotId.Block);
            Assert.That((IntPtr)window.GetAuxPtr(center), Is.Not.EqualTo(IntPtr.Zero));
            Assert.That((IntPtr)window.GetBrickPtr(center), Is.EqualTo((IntPtr)window.Center));
            Assert.That(window.GetBlock(new int3(1, 1, 1)), Is.EqualTo(Solid()));

            Assert.That((IntPtr)window.GetBrickPtr(neighbour), Is.EqualTo(IntPtr.Zero));
            Assert.That(
                (IntPtr)window.GetAuxPtr(neighbour), Is.EqualTo(IntPtr.Zero),
                "an unallocated neighbour has no aux slice");
        }

        // ---- 6. Keyed voxel-slot write --------------------------------------------------------------

        [Test]
        public void WriteVoxelSlotsWritesTheExistingMaskedSlots()
        {
            using var scope = new EntityDataTestScope();
            var pos = new int3(3, 4, 5);
            scope.Data.SetBlock(pos, new Block(0x8007));
            scope.Data.SetSlot(SectorSlotId.Reserved1, pos, (short)-9);

            var writer = new NetMessageWriter();
            Assert.That(scope.Data.WriteVoxelSlots(writer, pos, 0xFFFF), Is.True);

            var reader = new NetMessageReader(writer.Buffer, writer.Length);
            Assert.That(reader.Read<byte>(), Is.EqualTo(2));

            Assert.That(reader.Read<byte>(), Is.EqualTo((byte)SectorSlotId.Block));
            Assert.That(reader.Read<ushort>(), Is.EqualTo(2));
            Assert.That(reader.Read<Block>(), Is.EqualTo(new Block(0x8007)));

            Assert.That(reader.Read<byte>(), Is.EqualTo((byte)SectorSlotId.Reserved1));
            Assert.That(reader.Read<ushort>(), Is.EqualTo(2));
            Assert.That(reader.Read<short>(), Is.EqualTo(-9));
            Assert.That(reader.Remaining, Is.EqualTo(0));

            var outside = new NetMessageWriter();
            Assert.That(scope.Data.WriteVoxelSlots(outside, new int3(1000, 0, 0), 0xFFFF), Is.False);
            Assert.That(outside.Length, Is.EqualTo(0), "an absent region writes nothing at all");
        }

        // ---- 7. Keyed preview ------------------------------------------------------------------------

        [Test]
        public void PreviewBuildByRegionMatchesTheStorageOverload()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 2, 3), Solid(0x1234));
            scope.Data.SetBlock(new int3(70, 2, 3), Solid(0x4321));

            var expected = new uint[Sector.BRICKS_IN_SECTOR];
            var actual = new uint[Sector.BRICKS_IN_SECTOR];
            PreviewBuilder.Build(scope.SectorAt(int3.zero).Ptr, expected);

            Assert.That(PreviewBuilder.Build(scope.Data, int3.zero, actual), Is.True);
            Assert.That(actual, Is.EqualTo(expected));

            Assert.That(PreviewBuilder.Build(scope.Data, new int3(9, 9, 9), actual), Is.False);
        }

        // ---- 8. Region-scoped enumeration ---------------------------------------------------------------

        [Test]
        public void EnumerateBricksResolvesASmallKeyRangeByLookup()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(0, 0, 0), Solid());                                           // key (0,0,0)
            scope.Data.SetBlock(VoxelRegion.OriginOf(new int3(1, 0, 0)) + new int3(1, 1, 1), Solid());  // key (16,0,0)
            scope.Data.SetBlock(VoxelRegion.OriginOf(new int3(1, 0, 0)) + new int3(70, 1, 1), Solid()); // key (24,0,0)
            scope.Data.SetBlock(VoxelRegion.OriginOf(new int3(5, 0, 0)) + new int3(2, 2, 2), Solid());  // key (80,0,0)

            List<int3> inRegion = Collect(scope.Data.EnumerateBricks(
                VoxelRegion.FirstKeyOf(new int3(1, 0, 0)), VoxelRegion.LastKeyOf(new int3(1, 0, 0))));
            Assert.That(inRegion.Count, Is.EqualTo(2));
            Assert.That(inRegion, Does.Contain(new int3(16, 0, 0)));
            Assert.That(inRegion, Does.Contain(new int3(24, 0, 0)));

            List<int3> single = Collect(
                scope.Data.EnumerateBricks(new int3(24, 0, 0), new int3(24, 0, 0)));
            Assert.That(single, Is.EqualTo(new List<int3> { new int3(24, 0, 0) }));

            List<int3> emptyRegion = Collect(scope.Data.EnumerateBricks(
                VoxelRegion.FirstKeyOf(new int3(3, 0, 0)), VoxelRegion.LastKeyOf(new int3(3, 0, 0))));
            Assert.That(emptyRegion, Is.Empty);

            List<int3> all = Collect(scope.Data.EnumerateBricks());
            Assert.That(all.Count, Is.EqualTo(4));
            Assert.That(all, Does.Contain(new int3(80, 0, 0)));
        }

        // ---- 9. A default entity answers everything -----------------------------------------------------

        [Test]
        public void DefaultEntityDataAnswersEveryQueryWithoutStorage()
        {
            VoxelEntityData data = default;

            Assert.That(data.RegionCount, Is.EqualTo(0));
            Assert.That(data.AllocatedBrickCount, Is.EqualTo(0));
            Assert.That(data.ChangeCount, Is.EqualTo(0));
            Assert.That(data.HasRegion(int3.zero), Is.False);
            Assert.That(data.TryGetStorageBounds(out int3 min, out int3 max), Is.False);
            Assert.That(min, Is.EqualTo(int3.zero));
            Assert.That(max, Is.EqualTo(int3.zero));
            Assert.That(data.TryOpenRegion(int3.zero, out VoxelRegion region), Is.False);
            Assert.That(region.IsCreated, Is.False);
            Assert.That(data.RemoveRegion(int3.zero), Is.False);
            data.EnsureRegion(int3.zero);
            Assert.That(data.RegionCount, Is.EqualTo(0), "a default entity has nowhere to put a region");

            NativeArray<int3> positions = data.GetRegionPositions(Allocator.Temp);
            try
            {
                Assert.That(positions.Length, Is.EqualTo(0));
            }
            finally
            {
                positions.Dispose();
            }

            Assert.That(Collect(data.EnumerateBricks()), Is.Empty);
            Assert.That(Collect(data.EnumerateBricks(int3.zero, new int3(15))), Is.Empty);
            Assert.That(CollectDirtyFlags(data, BrickUpdateFlags.All, true).Count, Is.EqualTo(0));
            Assert.That(CollectDirtyFlags(data, BrickUpdateFlags.All, false).Count, Is.EqualTo(0));

            Assert.That(data.IsBrickAllocated(int3.zero), Is.False);
            BrickCursor cursor = default;
            Assert.That(data.IsBrickAllocated(int3.zero, ref cursor), Is.False);
            Assert.That(data.TryBindBrick(SectorSlotId.Block, int3.zero, out Block* blocks), Is.False);
            Assert.That((IntPtr)blocks, Is.EqualTo(IntPtr.Zero));
            Assert.That(data.TryBindBrick(SectorSlotId.Block, int3.zero, ref cursor, out Block* cursorBlocks), Is.False);
            Assert.That((IntPtr)cursorBlocks, Is.EqualTo(IntPtr.Zero));
            Assert.That(data.TryBindBrickAux(SectorSlotId.Block, int3.zero, out void* aux), Is.False);
            Assert.That((IntPtr)aux, Is.EqualTo(IntPtr.Zero));
            Assert.That(data.TryBindBrickAux(SectorSlotId.Block, int3.zero, ref cursor, out void* cursorAux), Is.False);
            Assert.That((IntPtr)cursorAux, Is.EqualTo(IntPtr.Zero));

            Assert.That(data.GetRequiredFlags(int3.zero), Is.EqualTo(BrickUpdateFlags.None));
            data.MarkBrickDirty(int3.zero, BrickUpdateFlags.Geometry);
            Assert.That(data.MarkRequired(int3.zero, BrickUpdateFlags.Geometry), Is.False);

            int bitmask = 0;
            foreach (BrickSlotEntry<Block> _ in
                     data.EnumerateBrickBitmask<Block>(SectorSlotId.Block, int3.zero))
            {
                bitmask++;
            }

            Assert.That(bitmask, Is.EqualTo(0));

            using (var covered = new NativeList<int3>(Allocator.Persistent))
            {
                data.EnsureSlot<PhysicsInfo>(SectorSlotId.PhysicsInfo, BrickBitmask.Bytes, covered);
                Assert.That(covered.Length, Is.EqualTo(0));
            }

            Assert.That(data.WriteVoxelSlots(new NetMessageWriter(), int3.zero, 0xFFFF), Is.False);

            VoxelNeighborhood window = data.OpenNeighborhood(int3.zero);
            Assert.That((IntPtr)window.Center, Is.EqualTo(IntPtr.Zero));
            Assert.That(window.GetBlock(int3.zero), Is.EqualTo(Block.Empty));

            var preview = new uint[Sector.BRICKS_IN_SECTOR];
            Assert.That(PreviewBuilder.Build(data, int3.zero, preview), Is.False);
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

        private static Dictionary<int3, BrickUpdateFlags> CollectDirtyFlags(
            VoxelEntityData data, BrickUpdateFlags mask, bool dirtyOnly)
        {
            var found = new Dictionary<int3, BrickUpdateFlags>();
            foreach (BrickDirtyFlags entry in data.EnumerateBrickDirtyFlags(mask, dirtyOnly))
            {
                Assert.That(found.ContainsKey(entry.Key), Is.False, $"duplicate key {entry.Key}");
                found.Add(entry.Key, entry.Flags);
            }

            return found;
        }
    }
}

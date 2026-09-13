using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.IO;
using Caelix.Tests.TestSupport;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// Which simulation bits a voxel write raises: the <see cref="AutomataDefaults"/> tables on the
    /// default setters, the caller's mask on the explicit ones, engine geometry bits on every Block
    /// write, and <see cref="AutomataBrick.SetDirty"/> as a write-free scheduling request. Also the
    /// pieces the world's per-hook selection relies on (allocation status on collected records,
    /// masked snapshot activation) and the pre-v8 save conversion.
    /// </summary>
    public unsafe class AutomataFlagsTests
    {
        private const ushort BlockA = 0x8001;
        private const ushort BlockB = 0x8002;
        private const ushort BlockInert = 0x8003;
        private const SectorSlotId MetaSlot = (SectorSlotId)2;
        private const ushort Geometry = (ushort)DirtyPropagationSettings.BlockWriteGeometryFlags;

        private static readonly Guid128 EntityId = new Guid128(7, 0, 0, 0);

        private string _tempPath;

        [SetUp]
        public void SetUp()
        {
            // The tables are process-wide. Every test starts from a clean, known configuration.
            AutomataDefaults.Reset();
            AutomataDefaults.SetBlockDefault(BlockA, BrickUpdateFlags.Automata0);
            AutomataDefaults.SetBlockDefault(BlockB, BrickUpdateFlags.Automata1);
            AutomataDefaults.SetSlotDefault(MetaSlot, BrickUpdateFlags.Automata2);
            _tempPath = Path.Combine(Path.GetTempPath(), $"caelix_flags_{System.Guid.NewGuid():N}.cxw");
        }

        [TearDown]
        public void TearDown()
        {
            AutomataDefaults.Reset();
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
        }

        private static ushort DirtyAt(SectorHandle sector, int x, int y, int z)
            => sector.Get().brickDirtyFlags[Sector.ToBrickIdx(x >> 3, y >> 3, z >> 3)];

        private static void ClearDirty(SectorHandle sector) => sector.Get().ClearAllDirtyFlags();

        // ---- Default tables --------------------------------------------------------------------

        [Test]
        public void DefaultBlockWrite_RaisesGeometryPlusOldAndNewBlockRules()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                sector.SetBlock(1, 1, 1, new Block(BlockA));
                ushort placed = DirtyAt(sector, 1, 1, 1);
                Assert.That(placed & Geometry, Is.EqualTo(Geometry), "every Block write raises geometry");
                Assert.That(placed & (ushort)BrickUpdateFlags.AutomataMask,
                    Is.EqualTo((ushort)BrickUpdateFlags.Automata0), "air -> A raises A's rule only");

                ClearDirty(sector);
                sector.SetBlock(1, 1, 1, new Block(BlockB));
                Assert.That(DirtyAt(sector, 1, 1, 1) & (ushort)BrickUpdateFlags.AutomataMask,
                    Is.EqualTo((ushort)(BrickUpdateFlags.Automata0 | BrickUpdateFlags.Automata1)),
                    "A -> B raises both the removed and the introduced rule");

                ClearDirty(sector);
                sector.SetBlock(1, 1, 1, new Block(BlockInert));
                Assert.That(DirtyAt(sector, 1, 1, 1) & (ushort)BrickUpdateFlags.AutomataMask,
                    Is.EqualTo((ushort)BrickUpdateFlags.Automata1), "B -> unclassified raises B's rule (removal)");

                ClearDirty(sector);
                sector.SetBlock(1, 1, 1, Block.Empty);
                Assert.That(DirtyAt(sector, 1, 1, 1), Is.EqualTo(Geometry),
                    "unclassified -> air raises geometry only");
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void DefaultBlockWrite_UsesTheEmptyRuleForAirOnBothSides()
        {
            AutomataDefaults.SetBlockDefault(Block.Empty.id, BrickUpdateFlags.Automata5);
            var sector = SectorHandle.AllocEmpty();
            try
            {
                sector.SetBlock(1, 1, 1, new Block(BlockInert));
                Assert.That(DirtyAt(sector, 1, 1, 1) & (ushort)BrickUpdateFlags.AutomataMask,
                    Is.EqualTo((ushort)BrickUpdateFlags.Automata5), "air -> solid consults the air rule");

                ClearDirty(sector);
                sector.SetBlock(1, 1, 1, Block.Empty);
                Assert.That(DirtyAt(sector, 1, 1, 1) & (ushort)BrickUpdateFlags.AutomataMask,
                    Is.EqualTo((ushort)BrickUpdateFlags.Automata5), "solid -> air consults the air rule");
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void DefaultSlotWrite_UsesTheSlotRuleAndNoGeometry()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                sector.SetBlock(1, 1, 1, new Block(BlockInert));
                ClearDirty(sector);

                sector.SetSlot(MetaSlot, 1, 1, 1, (short)5);
                Assert.That(DirtyAt(sector, 1, 1, 1), Is.EqualTo((ushort)BrickUpdateFlags.Automata2),
                    "a Meta write raises the slot default and nothing else");
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

        // ---- Explicit masks --------------------------------------------------------------------

        [Test]
        public void ExplicitBlockWrite_IgnoresTheTablesAndKeepsGeometry()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                // Table says A -> Automata0; the writer says Automata7. Only the writer counts.
                // (The first write also allocates the brick, which raises BlockBrickAdded.)
                sector.SetBlock(1, 1, 1, new Block(BlockA), BrickUpdateFlags.Automata7);
                Assert.That(DirtyAt(sector, 1, 1, 1) & ~(ushort)BrickUpdateFlags.BlockBrickAdded,
                    Is.EqualTo((ushort)(Geometry | (ushort)BrickUpdateFlags.Automata7)));

                ClearDirty(sector);
                sector.SetBlock(1, 1, 1, new Block(BlockB), BrickUpdateFlags.None);
                Assert.That(DirtyAt(sector, 1, 1, 1), Is.EqualTo(Geometry),
                    "explicit None suppresses every simulation bit but not geometry");
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void ExplicitSlotWrite_RaisesExactlyTheMask()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                sector.SetBlock(1, 1, 1, new Block(BlockInert));
                ClearDirty(sector);

                sector.SetSlot(MetaSlot, 1, 1, 1, (short)5, BrickUpdateFlags.Automata9);
                Assert.That(DirtyAt(sector, 1, 1, 1), Is.EqualTo((ushort)BrickUpdateFlags.Automata9));

                ClearDirty(sector);
                sector.SetSlot(MetaSlot, 1, 1, 1, (short)6, BrickUpdateFlags.None);
                Assert.That(DirtyAt(sector, 1, 1, 1), Is.EqualTo((ushort)0), "None on a Meta write marks nothing");
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

        [Test]
        public void UnchangedValues_MarkNothing_OnBothPaths()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                sector.SetBlock(1, 1, 1, new Block(BlockA));
                ClearDirty(sector);

                sector.SetBlock(1, 1, 1, new Block(BlockA));
                sector.SetBlock(1, 1, 1, new Block(BlockA), BrickUpdateFlags.Automata3);
                Assert.That(sector.Get().sectorDirtyFlags, Is.EqualTo((ushort)0));

                // Air into an unallocated brick: no allocation, no flags, on either path.
                sector.SetBlock(64, 64, 64, Block.Empty);
                sector.SetBlock(72, 64, 64, Block.Empty, BrickUpdateFlags.Automata3);
                Assert.That(sector.Get().sectorDirtyFlags, Is.EqualTo((ushort)0));
                Assert.That(sector.Get().brickMap.indices[Sector.ToBrickIdx(8, 8, 8)], Is.EqualTo(Sector.BRICKID_EMPTY));
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void ExplicitMaskWithEngineBitsThrows()
        {
            var sector = SectorHandle.AllocEmpty();
            try
            {
                Assert.Throws<System.ArgumentException>(() =>
                    sector.SetBlock(1, 1, 1, new Block(BlockA), BrickUpdateFlags.Geometry));
            }
            finally { sector.Dispose(Allocator.Persistent); }
        }
#endif

        // ---- SetDirty --------------------------------------------------------------------------

        [Test]
        public void SetDirty_SchedulesNextTickWorkWithoutAWrite()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(5, 5, 5), new Block(BlockInert), BrickUpdateFlags.None);
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            var key = new int3(0, 0, 0);
            scope.Data.MarkRequired(key, BrickUpdateFlags.Automata1);
            scope.Data.BeginAutomataWrites(BrickUpdateFlags.Automata1);

            AutomataBrick brick = scope.Data.OpenAutomataBrick(key);
            brick.SetDirty(BrickUpdateFlags.Automata1);
            scope.Data.EndAutomataWrites();

            SectorHandle sector = scope.SectorAt(int3.zero);
            Assert.That(DirtyAt(sector, 5, 5, 5), Is.EqualTo((ushort)BrickUpdateFlags.Automata1),
                "only the requested simulation bit, no geometry or allocation bookkeeping");
            Assert.That(scope.Data.GetBlock(new int3(5, 5, 5)), Is.EqualTo(new Block(BlockInert)), "no voxel changed");

            // The tick clears what it consumed, then converts dirty into next-tick work.
            scope.Data.ClearRequireUpdates();
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            Assert.That(scope.Data.GetRequiredFlags(key) & BrickUpdateFlags.Automata1, Is.EqualTo(BrickUpdateFlags.Automata1));
            Assert.That(scope.Data.GetRequiredFlags(new int3(1, 0, 0)) & BrickUpdateFlags.Automata1,
                Is.EqualTo(BrickUpdateFlags.Automata1), "whole-brick propagation reaches the neighbours");
            Assert.That(scope.Data.GetRequiredFlags(key) & BrickUpdateFlags.Geometry, Is.EqualTo(BrickUpdateFlags.None));
        }

        // ---- Selection support -----------------------------------------------------------------

        [Test]
        public void CollectedRecords_ReportAllocationStatus()
        {
            using var scope = new EntityDataTestScope();
            // (7,5,5) sits on the +X face of brick (0,0,0), so propagation also flags the
            // unallocated brick (1,0,0).
            scope.Data.SetBlock(new int3(7, 5, 5), new Block(BlockA));
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();

            var into = new NativeList<RequiredBrick>(Allocator.TempJob);
            try
            {
                scope.Data.CollectRequiredBricks(EntityId, BrickUpdateFlags.Automata0, includeEmpty: true, into);
                bool sawAllocated = false, sawEmpty = false;
                for (int i = 0; i < into.Length; i++)
                {
                    RequiredBrick record = into[i];
                    if (record.Key.Equals(new int3(0, 0, 0)))
                    {
                        Assert.That(record.IsAllocated, Is.True);
                        sawAllocated = true;
                    }
                    else if (record.Key.Equals(new int3(1, 0, 0)))
                    {
                        Assert.That(record.IsAllocated, Is.False, "the flagged neighbour holds no brick");
                        sawEmpty = true;
                    }
                }

                Assert.That(sawAllocated, Is.True);
                Assert.That(sawEmpty, Is.True);

                into.Clear();
                scope.Data.CollectRequiredBricks(EntityId, BrickUpdateFlags.Automata0, includeEmpty: false, into);
                for (int i = 0; i < into.Length; i++)
                {
                    Assert.That(into[i].IsAllocated, Is.True, "without empty positions every record is allocated");
                }
            }
            finally { into.Dispose(); }
        }

        [Test]
        public void BeginAutomataWrites_WithMask_SkipsSectorsWithForeignWorkOnly()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(5, 5, 5), new Block(BlockInert), BrickUpdateFlags.None);
            scope.Data.PropagateDirtyFlags(BrickUpdateFlags.All).Complete();
            SectorHandle sector = scope.SectorAt(int3.zero);
            Assert.That(sector.RequireUpdateFlags & (ushort)BrickUpdateFlags.Geometry, Is.Not.Zero, "geometry work is pending");

            scope.Data.BeginAutomataWrites(BrickUpdateFlags.AutomataMask);
            Assert.That(sector.Get().IsSnapshotEnabled, Is.False, "geometry-only work needs no pending buffer");
            scope.Data.EndAutomataWrites();

            scope.Data.BeginAutomataWrites();
            Assert.That(sector.Get().IsSnapshotEnabled, Is.True, "the unmasked overload keeps the old behaviour");
            scope.Data.EndAutomataWrites();
        }

        // ---- Legacy saves ----------------------------------------------------------------------

        private sealed class ScopeTarget : IWorldLoadTarget
        {
            public readonly EntityDataTestScope Scope = new();

            public bool TryCreateEntity(in EntityRecord record, out VoxelEntityData data)
            {
                data = Scope.Data;
                return true;
            }

            public void CommitEntity(in EntityRecord record, in VoxelEntityData data)
            {
                Scope.Data = data;
            }
        }

        [Test]
        public void Load_ConvertsPendingLegacyBitZero_OnlyInPreV8Saves()
        {
            AutomataDefaults.LegacyGeneralAutomataFlags = BrickUpdateFlags.Automata0 | BrickUpdateFlags.Automata1;

            var handle = SectorHandle.AllocEmpty();
            try
            {
                handle.SetBlock(0, 0, 0, new Block(BlockInert), BrickUpdateFlags.None);
                handle.SetBlock(64, 64, 64, new Block(BlockInert), BrickUpdateFlags.None);
                ref Sector source = ref handle.Get();
                source.brickRequireUpdateFlags[Sector.ToBrickIdx(0, 0, 0)] = (ushort)BrickUpdateFlags.Automata0; // "GeneralAutomata" pending
                source.brickRequireUpdateFlags[Sector.ToBrickIdx(8, 8, 8)] = (ushort)BrickUpdateFlags.Geometry;  // idle for automata
                source.sectorRequireUpdateFlags = (ushort)(BrickUpdateFlags.Automata0 | BrickUpdateFlags.Geometry);

                uint[] preview = new uint[Sector.BRICKS_IN_SECTOR];
                PreviewBuilder.Build(handle.Ptr, preview);
                byte[] payload = SectorSerializer.Pack(in source);
                using (var writer = SingleFileSaveStorage.OpenWrite(_tempPath))
                {
                    writer.WriteEntity(new EntityRecord(EntityId, default, (ushort)BrickUpdateFlags.Automata0),
                        new[] { new SectorWriteRecord(int3.zero, preview, payload) });
                    writer.Commit();
                }
            }
            finally { handle.Dispose(Allocator.Persistent); }

            // Current version: bit 0 is a channel and stays as written.
            AssertLoadedFlags(expectedPending: (ushort)BrickUpdateFlags.Automata0);

            // Stamp the header as v7: bit 0 was GeneralAutomata and becomes the configured channels.
            using (var stream = new FileStream(_tempPath, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = 4;
                stream.WriteByte(7);
                stream.WriteByte(0);
            }

            AssertLoadedFlags(expectedPending: (ushort)(BrickUpdateFlags.Automata0 | BrickUpdateFlags.Automata1));
        }

        private void AssertLoadedFlags(ushort expectedPending)
        {
            var target = new ScopeTarget();
            try
            {
                WorldLoader.Load(_tempPath, target);
                ushort active = target.Scope.RequireFlagsAt(int3.zero, new int3(0, 0, 0));
                ushort idle = target.Scope.RequireFlagsAt(int3.zero, new int3(8, 8, 8));
                Assert.That(active & (ushort)BrickUpdateFlags.AutomataMask, Is.EqualTo(expectedPending));
                Assert.That(idle & (ushort)BrickUpdateFlags.AutomataMask, Is.EqualTo((ushort)0), "an idle brick never wakes on load");
                Assert.That(target.Scope.Data.entityRequireUpdateFlags & (ushort)BrickUpdateFlags.AutomataMask, Is.EqualTo(expectedPending));
            }
            finally { target.Scope.Dispose(); }
        }
    }
}

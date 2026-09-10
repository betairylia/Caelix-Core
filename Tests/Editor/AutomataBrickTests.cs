using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.Utils;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    /// <summary>
    /// <see cref="AutomataBrick"/> and the automata phase on <see cref="VoxelEntityData"/>: front
    /// buffer reads, pending writes, the center-brick write rule, and alien-aware reads through
    /// <see cref="AutomataReader"/>.
    /// </summary>
    public unsafe class AutomataBrickTests
    {
        private static readonly Guid128 SelfEntityId = new Guid128(1, 0, 0, 0);
        private static readonly Guid128 AlienEntityId = new Guid128(2, 0, 0, 0);

        private static Block Solid(int id = 1) => new Block((ushort)id);

        // ---- 1. Double buffering ---------------------------------------------------------------

        [Test]
        public void WritesLandInThePendingBufferUntilTheStageEnds()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(5, 5, 5), Solid(1));
            scope.Data.SetBlock(new int3(6, 5, 5), Solid(3));
            scope.Data.SetBlock(new int3(8, 5, 5), Solid(2));   // the next brick over
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            var key = new int3(0, 0, 0);
            Assert.That(scope.Data.MarkRequired(key, DirtyFlags.GeneralAutomata), Is.True);
            scope.Data.BeginAutomataWrites();

            AutomataBrick brick = scope.Data.OpenAutomataBrick(key);
            Assert.That(brick.Key, Is.EqualTo(key));
            Assert.That(brick.Origin, Is.EqualTo(new int3(0, 0, 0)));
            Assert.That(brick.IsAllocated, Is.True);

            Assert.That(brick.GetBlock(new int3(5, 5, 5)), Is.EqualTo(Solid(1)));
            Assert.That(brick.GetBlock(new int3(8, 5, 5)), Is.EqualTo(Solid(2)), "reads span the window");
            Assert.That(brick.GetSlot<Block>(SectorSlotId.Block, new int3(8, 5, 5)), Is.EqualTo(Solid(2)));

            brick.SetBlock(new int3(6, 5, 5), Solid(4));
            Assert.That(
                brick.GetBlock(new int3(6, 5, 5)), Is.EqualTo(Solid(3)),
                "reads see the front buffer until the stage ends");

            scope.Data.EndAutomataWrites();
            Assert.That(scope.Data.GetBlock(new int3(6, 5, 5)), Is.EqualTo(Solid(4)));
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [Test]
        public void WritingOutsideTheCenterBrickThrows()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(5, 5, 5), Solid(1));
            scope.Data.SetBlock(new int3(8, 5, 5), Solid(2));
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.MarkRequired(new int3(0, 0, 0), DirtyFlags.GeneralAutomata);
            scope.Data.BeginAutomataWrites();

            AutomataBrick brick = scope.Data.OpenAutomataBrick(new int3(0, 0, 0));
            Assert.Throws<IndexOutOfRangeException>(() => brick.SetBlock(new int3(8, 5, 5), Solid(9)));
            Assert.Throws<IndexOutOfRangeException>(() => brick.SetBlock(new int3(-1, 0, 0), Solid(9)));

            scope.Data.EndAutomataWrites();
        }

        [Test]
        public void WritingIntoABrickWithNoRegionThrows()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(1, 1, 1), Solid());

            AutomataBrick orphan = scope.Data.OpenAutomataBrick(new int3(1000, 0, 0));
            Assert.That(orphan.IsAllocated, Is.False);
            Assert.That(orphan.GetBlock(orphan.Origin), Is.EqualTo(Block.Empty));
            Assert.Throws<InvalidOperationException>(() => orphan.SetBlock(orphan.Origin, Solid()));
            Assert.That(scope.Data.HasRegion(new int3(7, 0, 0)), Is.False, "opening never allocates");
        }
#endif

        // ---- 2. Unallocated center brick ---------------------------------------------------------

        [Test]
        public void AnUnallocatedCenterBrickReadsEmptyAndAllocatesOnWrite()
        {
            using var scope = new EntityDataTestScope();
            scope.Data.SetBlock(new int3(5, 5, 5), Solid(1));   // region (0,0,0), brick (0,0,0)
            scope.Data.PropagateDirtyFlags(DirtyFlags.All).Complete();
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();
            scope.Data.MarkRequired(new int3(0, 0, 0), DirtyFlags.GeneralAutomata);
            scope.Data.BeginAutomataWrites();

            var key = new int3(1, 1, 1);
            AutomataBrick brick = scope.Data.OpenAutomataBrick(key);
            int3 target = brick.Origin + new int3(2, 2, 2);

            Assert.That(brick.IsAllocated, Is.False);
            Assert.That(brick.GetBlock(target), Is.EqualTo(Block.Empty));
            Assert.That(scope.Data.IsBrickAllocated(key), Is.False);

            brick.SetBlock(target, Solid(7));
            scope.Data.EndAutomataWrites();

            Assert.That(scope.Data.IsBrickAllocated(key), Is.True);
            Assert.That(scope.Data.GetBlock(target), Is.EqualTo(Solid(7)));
        }

        // ---- 3. Alien-aware reads ------------------------------------------------------------------

        [Test]
        public void AutomataReaderFallsBackToAnAlienBlockWhenTheLocalVoxelIsEmpty()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            self.AddSector(int3.zero);
            SectorHandle alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(4, 5, 6, new Block(11));

            var views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            var entities = new NativeHashMap<Guid128, VoxelEntityData>(2, Allocator.Temp);
            try
            {
                views[0] = BuildView(SelfEntityId, self.Data);
                views[1] = BuildView(AlienEntityId, alien.Data);
                entities.Add(SelfEntityId, self.Data);
                entities.Add(AlienEntityId, alien.Data);

                var context = new AutomataReadContext
                {
                    AlienQuery = new AlienOccupancyQuery { EntitiesInDeterministicOrder = views },
                    Entities = entities
                };

                var record = new RequiredBrick
                {
                    Entity = SelfEntityId,
                    Key = new int3(0, 0, 0),
                    Flags = DirtyFlags.GeneralAutomata,
                    LocalToWorld = RigidTransform.identity
                };

                AutomataBrick access = context.OpenBrick(record);
                AutomataReader reader = context.CreateReader(record, access);

                Assert.That(reader.GetLocalBlock(new int3(4, 5, 6)), Is.EqualTo(Block.Empty));
                Assert.That(reader.GetBlock(new int3(4, 5, 6)), Is.EqualTo(new Block(11)));
                Assert.That(reader.IsVoxelSpaceOccupied(new int3(4, 5, 6)), Is.True);
                Assert.That(reader.IsVoxelSpaceOccupied(new int3(1, 1, 1)), Is.False);
            }
            finally
            {
                entities.Dispose();
                views.Dispose();
            }
        }

        [Test]
        public void AutomataReaderPrefersTheLocalBlock()
        {
            using var self = new EntityDataTestScope();
            using var alien = new EntityDataTestScope();

            SectorHandle selfSector = self.AddSector(int3.zero);
            selfSector.SetBlock(1, 2, 3, new Block(7));
            SectorHandle alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(1, 2, 3, new Block(9));

            var views = new NativeArray<AlienEntityView>(2, Allocator.Temp);
            try
            {
                views[0] = BuildView(SelfEntityId, self.Data);
                views[1] = BuildView(AlienEntityId, alien.Data);

                var record = new RequiredBrick
                {
                    Entity = SelfEntityId,
                    Key = new int3(0, 0, 0),
                    LocalToWorld = RigidTransform.identity
                };

                AutomataReader reader = AutomataReader.Create(
                    record,
                    self.Data.OpenAutomataBrick(record.Key),
                    new AlienOccupancyQuery { EntitiesInDeterministicOrder = views });

                Assert.That(reader.GetBlock(new int3(1, 2, 3)), Is.EqualTo(new Block(7)));
            }
            finally
            {
                views.Dispose();
            }
        }

        [Test]
        public void AlienEntityViewReadsThroughTheEntityData()
        {
            using var alien = new EntityDataTestScope();
            SectorHandle alienSector = alien.AddSector(int3.zero);
            alienSector.SetBlock(4, 5, 6, new Block(11));

            AlienEntityView view = BuildView(AlienEntityId, alien.Data);
            Assert.That(view.GetBlockAtLocalVoxel(new int3(4, 5, 6)), Is.EqualTo(new Block(11)));
            Assert.That(view.GetSlotAtLocalVoxel<Block>(SectorSlotId.Block, new int3(4, 5, 6)),
                Is.EqualTo(new Block(11)));
            Assert.That(view.GetBlockAtLocalVoxel(new int3(400, 5, 6)), Is.EqualTo(Block.Empty));

            // A view with no storage reads as empty instead of throwing.
            AlienEntityView storageless = view;
            storageless.Data = default;
            Assert.That(storageless.GetBlockAtLocalVoxel(new int3(4, 5, 6)), Is.EqualTo(Block.Empty));
            Assert.That(storageless.GetSlotAtLocalVoxel<Block>(SectorSlotId.Block, new int3(4, 5, 6)),
                Is.EqualTo(default(Block)));
        }

        private static AlienEntityView BuildView(Guid128 id, VoxelEntityData data)
        {
            return new AlienEntityView
            {
                EntityId = id,
                LocalToWorld = RigidTransform.identity,
                WorldToLocal = float4x4.identity,
                Data = data,
                WorldAabbMin = new float3(-512f),
                WorldAabbMax = new float3(512f)
            };
        }
    }
}

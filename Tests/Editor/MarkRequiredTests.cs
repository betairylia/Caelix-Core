using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;
using Caelix.Tests.TestSupport;
using Caelix.Utils;

namespace Caelix.Tests
{
    /// <summary>
    /// <see cref="VoxelEntityData.MarkRequired"/> marks EXISTING bricks only: a sector that exists
    /// but holds no brick at the key must not gain automata work.
    /// </summary>
    public class MarkRequiredTests
    {
        private static readonly Guid128 EntityId = new Guid128(7, 0, 0, 0);

        [Test]
        public void MarkRequired_IgnoresUnallocatedBrickInExistingSector()
        {
            using var scope = new EntityDataTestScope();
            scope.AddSector(int3.zero);
            scope.Data.SetBlock(new int3(1, 1, 1), new Block(5)); // brick (0,0,0) exists

            scope.Data.MarkRequired(new int3(3, 0, 0), DirtyFlags.GeneralAutomata); // brick (3,0,0) does not

            using var into = new NativeList<RequiredBrick>(8, Allocator.Persistent);
            scope.Data.CollectRequiredBricks(EntityId, DirtyFlags.GeneralAutomata, includeEmpty: true, into);

            Assert.That(into.Length, Is.EqualTo(0));
            Assert.That(scope.RequireFlagsAt(int3.zero, new int3(3, 0, 0)), Is.EqualTo(0));
        }

        [Test]
        public void MarkRequired_IgnoresMissingSector()
        {
            using var scope = new EntityDataTestScope();

            Assert.DoesNotThrow(() => scope.Data.MarkRequired(new int3(40, 0, 0), DirtyFlags.GeneralAutomata));
            Assert.That(scope.Data.sectors.Count, Is.EqualTo(0));
        }

        [Test]
        public void MarkRequired_MarksAllocatedBrick()
        {
            using var scope = new EntityDataTestScope();
            scope.AddSector(int3.zero);
            scope.Data.SetBlock(new int3(25, 1, 1), new Block(5)); // brick (3,0,0)
            scope.Data.ClearDirtyFlags();
            scope.Data.ClearRequireUpdates();

            scope.Data.MarkRequired(new int3(3, 0, 0), DirtyFlags.GeneralAutomata);

            using var into = new NativeList<RequiredBrick>(8, Allocator.Persistent);
            scope.Data.CollectRequiredBricks(EntityId, DirtyFlags.GeneralAutomata, includeEmpty: false, into);

            Assert.That(into.Length, Is.EqualTo(1));
            Assert.That(into[0].Key, Is.EqualTo(new int3(3, 0, 0)));
            Assert.That(into[0].Entity, Is.EqualTo(EntityId));
            Assert.That((into[0].Flags & DirtyFlags.GeneralAutomata) != 0, Is.True);
        }

        [Test]
        public void SetSlot_AdvancesStorageEpoch()
        {
            using var scope = new EntityDataTestScope();
            scope.AddSector(int3.zero);
            uint before = scope.Data.StorageEpoch;

            scope.Data.SetBlock(new int3(1, 1, 1), new Block(5));

            Assert.That(scope.Data.StorageEpoch, Is.GreaterThan(before));
        }
    }
}

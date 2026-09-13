using NUnit.Framework;
using Unity.Mathematics;
using Caelix;
using Caelix.Tests.TestSupport;

namespace Caelix.Tests
{
    public class DirtyPropagationScenarioTests
    {
        [Test]
        public void DirtyBrickMarksItselfRequireUpdate()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(64, 64, 64).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(8, 8, 8)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtBrickEdgePropagatesToAdjacentBrick()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(7, 7, 7).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(1, 1, 1)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtSectorFacePropagatesToExistingNeighborSector()
        {
            using var world = new DirtyPropagationTestWorld();
            world.AddSector(new int3(1, 0, 0));

            world.SetBlock(127, 64, 64).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(1, 0, 0), new int3(0, 8, 8)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirtyVoxelAtSectorFaceCreatesMissingNeighborForPropagation()
        {
            using var world = new DirtyPropagationTestWorld();

            world.SetBlock(127, 64, 64).Propagate();

            Assert.That(world.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.True);
            Assert.That(world.RequireFlagsAt(new int3(1, 0, 0), new int3(0, 8, 8)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
        }

        [Test]
        public void DirectionMaskFiltersPropagationDirection()
        {
            using var world = new DirtyPropagationTestWorld();
            uint positiveXOnly = 1u << NeighborhoodSettings.DirectionToIndexMinusOne(new int3(1, 0, 0));

            world.MarkBrickDirty(new int3(5, 5, 5), BrickUpdateFlags.GeneralAutomata, positiveXOnly).Propagate();

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(6, 5, 5)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(4, 5, 5)) & (ushort)BrickUpdateFlags.GeneralAutomata, Is.EqualTo(0));
        }

        [Test]
        public void FlagFilterOnlyPropagatesRequestedFlags()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(5, 5, 5), BrickUpdateFlags.GeneralAutomata | BrickUpdateFlags.Reserved1).Propagate(BrickUpdateFlags.GeneralAutomata);

            ushort flags = world.RequireFlagsAt(new int3(0, 0, 0), new int3(6, 5, 5));
            Assert.That(flags & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(flags & (ushort)BrickUpdateFlags.Reserved1, Is.EqualTo(0));

            ushort selfFlags = world.RequireFlagsAt(new int3(0, 0, 0), new int3(5, 5, 5));
            Assert.That(selfFlags & (ushort)BrickUpdateFlags.GeneralAutomata, Is.Not.EqualTo(0));
            Assert.That(selfFlags & (ushort)BrickUpdateFlags.Reserved1, Is.EqualTo(0));
        }

        [Test]
        public void FlagWithoutNeighborPropagationOnlyMarksDirtyBrickRequireUpdate()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(5, 5, 5), BrickUpdateFlags.Geometry).Propagate(BrickUpdateFlags.Geometry);

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(5, 5, 5)) & (ushort)BrickUpdateFlags.Geometry, Is.Not.EqualTo(0));
            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(6, 5, 5)) & (ushort)BrickUpdateFlags.Geometry, Is.EqualTo(0));
        }

        [Test]
        public void FlagWithoutLocalAllocationPropagatesToExistingNeighborSector()
        {
            using var world = new DirtyPropagationTestWorld();
            world.AddSector(new int3(1, 0, 0));

            world.MarkBrickDirty(new int3(15, 8, 8), BrickUpdateFlags.GeometryWithLocalNeighbor).Propagate(BrickUpdateFlags.GeometryWithLocalNeighbor);

            Assert.That(world.RequireFlagsAt(new int3(1, 0, 0), new int3(0, 8, 8)) & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));
        }

        [Test]
        public void FlagWithoutLocalAllocationDoesNotCreateMissingNeighborSector()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(15, 8, 8), BrickUpdateFlags.GeometryWithLocalNeighbor).Propagate(BrickUpdateFlags.GeometryWithLocalNeighbor);

            Assert.That(world.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.False);
        }

        [Test]
        public void RemovingSectorMarksOnlySurvivingFacingBricksForUpdate()
        {
            using var world = new DirtyPropagationTestWorld();
            world.AddSector(new int3(1, 0, 0));
            world.SetBlock(127, 64, 64); // sector (0,0,0), brick (15,8,8): faces the sector at +X
            world.SetBlock(64, 64, 64);  // sector (0,0,0), brick (8,8,8): interior
            // The helper writes into the center sector, so the removed sector is filled directly.
            world.SectorAt(new int3(1, 0, 0)).SetBlock(64, 64, 64, new Block(1));
            world.Propagate(BrickUpdateFlags.All);
            world.Data.ClearDirtyFlags();
            world.Data.ClearRequireUpdates();

            world.Data.RemoveSectorAt(new int3(1, 0, 0));
            world.Propagate(BrickUpdateFlags.GeometryWithLocalNeighbor);

            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(15, 8, 8)) & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor, Is.Not.EqualTo(0));
            Assert.That(world.RequireFlagsAt(new int3(0, 0, 0), new int3(8, 8, 8)) & (ushort)BrickUpdateFlags.GeometryWithLocalNeighbor, Is.EqualTo(0));
            Assert.That(world.Data.sectors.ContainsKey(new int3(1, 0, 0)), Is.False);
            Assert.That(world.Data.sectors.Count, Is.EqualTo(1), "removal must not create sectors");
        }

        [Test]
        public void MissingNeighborWithoutBoundaryCreationDoesNotCrash()
        {
            using var world = new DirtyPropagationTestWorld();

            world.MarkBrickDirty(new int3(15, 8, 8), BrickUpdateFlags.GeneralAutomata, 0);

            Assert.DoesNotThrow(() => world.Propagate());
        }
    }
}

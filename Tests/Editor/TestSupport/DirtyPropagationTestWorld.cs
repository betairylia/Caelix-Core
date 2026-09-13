using System;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;

namespace Caelix.Tests.TestSupport
{
    public unsafe sealed class DirtyPropagationTestWorld : IDisposable
    {
        private readonly EntityDataTestScope entity;

        public DirtyPropagationTestWorld()
        {
            entity = new EntityDataTestScope();
            entity.AddSector(new int3(0, 0, 0));
        }

        public VoxelEntityData Data => entity.Data;

        internal SectorHandle Center => entity.SectorAt(new int3(0, 0, 0));

        public DirtyPropagationTestWorld AddSector(int3 sectorPos)
        {
            entity.AddSector(sectorPos);
            return this;
        }

        public DirtyPropagationTestWorld SetBlock(int x, int y, int z, ushort id = 1)
        {
            Center.SetBlock(x, y, z, new Block(id), BrickUpdateFlags.Automata0); // explicit: these tests exercise propagation, not the default tables
            return this;
        }

        public DirtyPropagationTestWorld MarkBrickDirty(int3 brickPos, BrickUpdateFlags flags, uint directionMask = 0xFFFFFFFF)
        {
            Center.Get().MarkBrickDirty(Caelix.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z), flags, directionMask);
            return this;
        }

        public void Propagate(BrickUpdateFlags flags = BrickUpdateFlags.Automata0)
        {
            entity.Data.PropagateDirtyFlags(flags).Complete();
        }

        internal SectorHandle SectorAt(int3 sectorPos)
        {
            return entity.SectorAt(sectorPos);
        }

        public ushort RequireFlagsAt(int3 sectorPos, int3 brickPos)
        {
            return entity.RequireFlagsAt(sectorPos, brickPos);
        }

        public void Dispose()
        {
            entity.Dispose();
        }
    }
}

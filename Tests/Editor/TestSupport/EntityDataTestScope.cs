using System;
using Unity.Collections;
using Unity.Mathematics;
using Caelix;

namespace Caelix.Tests.TestSupport
{
    public unsafe sealed class EntityDataTestScope : IDisposable
    {
        public VoxelEntityData Data;

        public EntityDataTestScope()
        {
            Data = new VoxelEntityData(Allocator.Persistent);
            // VoxelEntityData defaults to static (the safe authoring default). Physics tests exercise
            // mass properties and contacts, which only exist for moving entities, so scoped test data
            // is dynamic; a test that wants the static path sets Data.isStatic itself.
            Data.isStatic = false;
        }

        internal SectorHandle AddSector(int3 sectorPos)
        {
            var handle = SectorHandle.AllocEmpty();
            try
            {
                Data.AddSectorAt(sectorPos, handle);
            }
            catch
            {
                handle.Dispose(Allocator.Persistent);
                throw;
            }
            return handle;
        }

        internal SectorHandle SectorAt(int3 sectorPos)
        {
            return Data.sectors[sectorPos];
        }

        internal SectorNeighborHandles NeighborsAt(int3 sectorPos)
        {
            return Data.sectorNeighbors[sectorPos];
        }

        public ushort RequireFlagsAt(int3 sectorPos, int3 brickPos)
        {
            var sector = Data.sectors[sectorPos];
            int brickIdx = Caelix.Sector.ToBrickIdx(brickPos.x, brickPos.y, brickPos.z);
            return sector.Get().brickRequireUpdateFlags[brickIdx];
        }

        public void Dispose()
        {
            Data.Dispose();
        }
    }
}

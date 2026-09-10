using Unity.Collections;
using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix
{
    public struct AlienEntityView
    {
        public Guid128 EntityId;
        public RigidTransform LocalToWorld;
        public float4x4 WorldToLocal;
        [ReadOnly] public SharedHashMap<int3, SectorHandle> Sectors;
        public float3 WorldAabbMin;
        public float3 WorldAabbMax;

        /// <summary>
        /// The alien entity's storage, read through the brick-key facade. A view that carries it
        /// resolves every read through <see cref="VoxelEntityData"/>; a view built the older way,
        /// with <see cref="Sectors"/> alone, still works and is what the fallback below is for.
        /// </summary>
        public VoxelEntityData Data;

        public bool ContainsWorldPoint(float3 worldPoint)
        {
            return math.all(worldPoint >= WorldAabbMin) && math.all(worldPoint <= WorldAabbMax);
        }

        public Block GetBlockAtLocalVoxel(int3 localVoxel)
        {
            if (Data.sectors.IsCreated)
            {
                return Data.GetBlock(localVoxel);
            }

            int3 sectorPos = localVoxel >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS);
            if (!Sectors.TryGetValue(sectorPos, out SectorHandle sector))
            {
                return Block.Empty;
            }

            int mask = Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS);
            return sector.GetBlock(localVoxel.x & mask, localVoxel.y & mask, localVoxel.z & mask);
        }

        /// <summary>
        /// One slot value at an entity-local block position of the ALIEN entity, or
        /// <c>default</c> where it has no storage there.
        /// </summary>
        public T GetSlotAtLocalVoxel<T>(SectorSlotId slotId, int3 localVoxel) where T : unmanaged
        {
            if (Data.sectors.IsCreated)
            {
                return Data.GetSlot<T>(slotId, localVoxel);
            }

            int3 sectorPos = localVoxel >> (Sector.SHIFT_IN_BLOCKS + Sector.SHIFT_IN_BRICKS);
            if (!Sectors.TryGetValue(sectorPos, out SectorHandle sector))
            {
                return default;
            }

            int mask = Sector.BRICK_MASK | (Sector.SECTOR_MASK << Sector.SHIFT_IN_BLOCKS);
            return sector.GetSlot<T>(slotId, localVoxel.x & mask, localVoxel.y & mask, localVoxel.z & mask);
        }
    }

    public struct AlienOccupancyQuery
    {
        [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;

        public Block GetFirstOccupyingAlienBlock(Guid128 selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            float3 localSamplePoint = selfEntityLocalVoxel + new float3(0.5f);
            float3 worldSamplePoint = math.transform(selfLocalToWorld, localSamplePoint);

            for (int i = 0; i < EntitiesInDeterministicOrder.Length; i++)
            {
                AlienEntityView entity = EntitiesInDeterministicOrder[i];
                if (entity.EntityId == selfEntityId || !entity.ContainsWorldPoint(worldSamplePoint))
                {
                    continue;
                }

                float3 alienLocalPoint = math.mul(entity.WorldToLocal, new float4(worldSamplePoint, 1f)).xyz;
                int3 alienVoxel = (int3)math.floor(alienLocalPoint);
                Block alienBlock = entity.GetBlockAtLocalVoxel(alienVoxel);
                if (!alienBlock.isEmpty)
                {
                    return alienBlock;
                }
            }

            return Block.Empty;
        }

        public bool IsVoxelSpaceOccupied(Guid128 selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            return !GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel).isEmpty;
        }
    }

    public struct VoxelNeighborhoodReader
    {
        private Guid128 selfEntityId;
        private int3 centerSectorPos;
        private RigidTransform selfLocalToWorld;
        private SectorNeighborhoodReaderHelper localReader;
        private AlienOccupancyQuery alienQuery;

        public static VoxelNeighborhoodReader Create(BrickInfo brickInfo, AlienOccupancyQuery alienQuery)
        {
            return new VoxelNeighborhoodReader
            {
                selfEntityId = brickInfo.EntityId,
                centerSectorPos = brickInfo.SectorPos,
                selfLocalToWorld = brickInfo.LocalToWorld,
                localReader = new SectorNeighborhoodReaderHelper(brickInfo.Sector, brickInfo.Neighbors),
                alienQuery = alienQuery
            };
        }

        public Block GetLocalBlock(int3 sectorLocalVoxel) => localReader.GetBlock(sectorLocalVoxel);

        public Block GetBlock(int3 sectorLocalVoxel)
        {
            Block localBlock = localReader.GetBlock(sectorLocalVoxel);
            if (!localBlock.isEmpty)
            {
                return localBlock;
            }

            int3 selfEntityLocalVoxel = centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + sectorLocalVoxel;
            return alienQuery.GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel);
        }

        public bool IsVoxelSpaceOccupied(int3 sectorLocalVoxel)
        {
            if (!localReader.GetBlock(sectorLocalVoxel).isEmpty)
            {
                return true;
            }

            int3 selfEntityLocalVoxel = centerSectorPos * Sector.SECTOR_SIZE_IN_BLOCKS + sectorLocalVoxel;
            return alienQuery.IsVoxelSpaceOccupied(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel);
        }
    }

    /// <summary>
    /// Alien-aware reads for one automata brick, in ENTITY-LOCAL block coordinates. The keyed
    /// successor of <see cref="VoxelNeighborhoodReader"/>, which reads sector-local ones.
    /// </summary>
    /// <remarks>
    /// Local reads go through the brick's own window (front buffer, see
    /// <see cref="AutomataBrick"/>); a voxel that is empty locally is then looked up in the other
    /// entities that overlap it, in the deterministic order the query was built with.
    /// </remarks>
    public unsafe struct AutomataReader
    {
        private Guid128 selfEntityId;
        private RigidTransform selfLocalToWorld;
        private AutomataBrick access;
        private AlienOccupancyQuery alienQuery;

        /// <summary>Binds a work record and its opened brick to an alien query.</summary>
        public static AutomataReader Create(
            in RequiredBrick brick, in AutomataBrick access, AlienOccupancyQuery alienQuery)
        {
            return new AutomataReader
            {
                selfEntityId = brick.Entity,
                selfLocalToWorld = brick.LocalToWorld,
                access = access,
                alienQuery = alienQuery
            };
        }

        /// <summary>This entity's own block at an entity-local position, ignoring other entities.</summary>
        public Block GetLocalBlock(int3 blockPos) => access.GetBlock(blockPos);

        /// <summary>
        /// This entity's own block at an entity-local position, or the first block of another
        /// entity occupying that voxel's world position when it is locally empty.
        /// </summary>
        public Block GetBlock(int3 blockPos)
        {
            Block localBlock = access.GetBlock(blockPos);
            if (!localBlock.isEmpty)
            {
                return localBlock;
            }

            return alienQuery.GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, blockPos);
        }

        /// <summary>True when this entity or any other occupies the voxel at an entity-local position.</summary>
        public bool IsVoxelSpaceOccupied(int3 blockPos)
        {
            if (!access.GetBlock(blockPos).isEmpty)
            {
                return true;
            }

            return alienQuery.IsVoxelSpaceOccupied(selfEntityId, selfLocalToWorld, blockPos);
        }
    }

    public unsafe struct AutomataReadContext
    {
        [ReadOnly] public AlienOccupancyQuery AlienQuery;

        /// <summary>
        /// Every entity of the world, so a keyed work record can be resolved to its storage. Read
        /// only: an automata stage writes through <see cref="AutomataBrick"/>, never through this.
        /// </summary>
        [ReadOnly] public NativeHashMap<Guid128, VoxelEntityData> Entities;

        public VoxelNeighborhoodReader CreateReader(BrickInfo brickInfo)
        {
            return VoxelNeighborhoodReader.Create(brickInfo, AlienQuery);
        }

        /// <summary>
        /// Opens the brick a work record names. Returns an empty, write-dropping brick when the
        /// entity is unknown; see <see cref="VoxelEntityData.OpenAutomataBrick"/>.
        /// </summary>
        public AutomataBrick OpenBrick(in RequiredBrick brick)
        {
            VoxelEntityData data = default;
            if (Entities.IsCreated)
            {
                Entities.TryGetValue(brick.Entity, out data);
            }

            return data.OpenAutomataBrick(brick.Key);
        }

        /// <summary>Binds an opened brick to the alien query, in entity-local coordinates.</summary>
        public AutomataReader CreateReader(in RequiredBrick brick, in AutomataBrick access)
        {
            return AutomataReader.Create(brick, access, AlienQuery);
        }
    }
}

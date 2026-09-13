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
        public float3 WorldAabbMin;
        public float3 WorldAabbMax;

        /// <summary>
        /// The alien entity's storage. Every read below resolves through the brick-key facade on
        /// <see cref="VoxelEntityData"/>; a view with no storage reads as empty.
        /// </summary>
        public VoxelEntityData Data;

        public bool ContainsWorldPoint(float3 worldPoint)
        {
            return math.all(worldPoint >= WorldAabbMin) && math.all(worldPoint <= WorldAabbMax);
        }

        public Block GetBlockAtLocalVoxel(int3 localVoxel) => Data.GetBlock(localVoxel);

        /// <summary>
        /// One slot value at an entity-local block position of the ALIEN entity, or
        /// <c>default</c> where it has no storage there.
        /// </summary>
        public T GetSlotAtLocalVoxel<T>(SectorSlotId slotId, int3 localVoxel) where T : unmanaged
            => Data.GetSlot<T>(slotId, localVoxel);
    }

    public unsafe struct AlienOccupancyQuery
    {
        [ReadOnly] public NativeArray<AlienEntityView> EntitiesInDeterministicOrder;
        [ReadOnly] public NativeArray<int> CandidateIndices;
        private int2 candidateRange;

        /// <summary>Binds one source brick's candidate range. Keep this value only within the read phase.</summary>
        public AlienOccupancyQuery ForBrick(in RequiredBrick brick)
        {
            AlienOccupancyQuery result = this;
            result.candidateRange = brick.AlienCandidateRange;
            return result;
        }

        public Block GetFirstOccupyingAlienBlock(Guid128 selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            BrickCursor cursor = default;
            TryGetOccupant(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel, ref cursor,
                out Block block, out _, out _);
            return block;
        }

        internal bool TryGetOccupant(Guid128 selfEntityId, RigidTransform selfLocalToWorld,
            int3 selfEntityLocalVoxel, ref BrickCursor cursor,
            out Block block, out AlienEntityView occupant, out int3 occupantVoxel)
        {
            block = Block.Empty;
            occupant = default;
            occupantVoxel = default;
            // Standalone contexts can still supply a full ordered view list. World ticks always
            // supply a candidate buffer, including an explicit empty range for isolated bricks.
            int count = CandidateIndices.IsCreated ? candidateRange.y : EntitiesInDeterministicOrder.Length;
            if (count == 0) return false;
            float3 localSamplePoint = selfEntityLocalVoxel + new float3(0.5f);
            float3 worldSamplePoint = math.transform(selfLocalToWorld, localSamplePoint);

            for (int i = 0; i < count; i++)
            {
                int index = CandidateIndices.IsCreated ? CandidateIndices[candidateRange.x + i] : i;
                AlienEntityView entity = EntitiesInDeterministicOrder[index];
                if (entity.EntityId == selfEntityId || !entity.ContainsWorldPoint(worldSamplePoint))
                {
                    continue;
                }

                float3 alienLocalPoint = math.mul(entity.WorldToLocal, new float4(worldSamplePoint, 1f)).xyz;
                int3 alienVoxel = (int3)math.floor(alienLocalPoint);
                if (!entity.Data.TryBindBrick(SectorSlotId.Block, BrickKey.FromBlock(alienVoxel), ref cursor, out Block* values))
                    continue;
                int3 local = BrickKey.LocalBlock(alienVoxel);
                Block alienBlock = values[BrickKey.ToBlockIdx(local.x, local.y, local.z)];
                if (!alienBlock.isEmpty)
                {
                    block = alienBlock;
                    occupant = entity;
                    occupantVoxel = alienVoxel;
                    return true;
                }
            }

            return false;
        }

        public bool IsVoxelSpaceOccupied(Guid128 selfEntityId, RigidTransform selfLocalToWorld, int3 selfEntityLocalVoxel)
        {
            return !GetFirstOccupyingAlienBlock(selfEntityId, selfLocalToWorld, selfEntityLocalVoxel).isEmpty;
        }
    }

    /// <summary>
    /// Alien-aware reads for one automata brick, in ENTITY-LOCAL block coordinates.
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
        private BrickCursor cursor;

        /// <summary>Binds a work record and its opened brick to an alien query.</summary>
        public static AutomataReader Create(
            in RequiredBrick brick, in AutomataBrick access, AlienOccupancyQuery alienQuery)
        {
            return new AutomataReader
            {
                selfEntityId = brick.Entity,
                selfLocalToWorld = brick.LocalToWorld,
                access = access,
                alienQuery = alienQuery.ForBrick(brick)
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

            alienQuery.TryGetOccupant(selfEntityId, selfLocalToWorld, blockPos, ref cursor,
                out Block block, out _, out _);
            return block;
        }

        /// <summary>
        /// Reads metadata from the same local-first occupant selection as GetBlock. The returned
        /// position is that occupant's voxel center. Cursors are owned by this reader and must not
        /// survive EndAutomataWrites; no slot pointer is retained across calls.
        /// </summary>
        public T GetSlot<T>(SectorSlotId slot, int3 blockPos, out float3 worldPosition)
            where T : unmanaged, System.IEquatable<T>
        {
            worldPosition = math.transform(selfLocalToWorld, blockPos + new float3(0.5f));
            if (!access.GetBlock(blockPos).isEmpty) return access.GetSlot<T>(slot, blockPos);
            if (!alienQuery.TryGetOccupant(selfEntityId, selfLocalToWorld, blockPos, ref cursor,
                    out _, out AlienEntityView entity, out int3 voxel)) return default;
            worldPosition = math.transform(entity.LocalToWorld, voxel + new float3(0.5f));
            if (!entity.Data.TryBindBrick(slot, BrickKey.FromBlock(voxel), ref cursor, out T* values)) return default;
            int3 local = BrickKey.LocalBlock(voxel);
            return values[BrickKey.ToBlockIdx(local.x, local.y, local.z)];
        }

        /// <summary>True when this entity or any other occupies the voxel at an entity-local position.</summary>
        public bool IsVoxelSpaceOccupied(int3 blockPos)
        {
            if (!access.GetBlock(blockPos).isEmpty)
            {
                return true;
            }

            return !GetBlock(blockPos).isEmpty;
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

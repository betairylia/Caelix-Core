using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// A fixed-size block region of one entity, in entity-local coordinates. Generators fill a
    /// detached region in jobs and attach it with <see cref="VoxelEntityData.AttachRegion"/>, or
    /// open an attached one with <see cref="VoxelEntityData.TryOpenRegion"/> and write in place.
    /// Writes are thread-safe per brick. The size is a storage constant: never hardcode it.
    /// </summary>
    /// <remarks>
    /// The region is the residency and generation unit: the granularity at which storage is
    /// created, attached and freed. It is backed by one sector, and nothing public says so — a
    /// consumer addresses it by region position and by entity-local block position only.
    /// </remarks>
    public unsafe struct VoxelRegion
    {
        /// <summary>Bits a block position is shifted right by to get its region position.</summary>
        public const int Shift = BrickKey.Shift + Sector.SHIFT_IN_BRICKS;

        /// <summary>Blocks along one axis of a region.</summary>
        public const int SizeInBlocks = 1 << Shift;

        /// <summary>Bricks along one axis of a region.</summary>
        public const int SizeInBricks = 1 << Sector.SHIFT_IN_BRICKS;

        /// <summary>Position of the region that contains an entity-local <paramref name="blockPos"/>.</summary>
        public static int3 Of(int3 blockPos) => blockPos >> Shift;

        /// <summary>Position of the region that contains the brick at <paramref name="key"/>.</summary>
        public static int3 OfKey(int3 key) => key >> Sector.SHIFT_IN_BRICKS;

        /// <summary>Entity-local block position of a region's first block.</summary>
        public static int3 OriginOf(int3 regionPos) => regionPos << Shift;

        /// <summary>Key of a region's first brick.</summary>
        public static int3 FirstKeyOf(int3 regionPos) => regionPos << Sector.SHIFT_IN_BRICKS;

        /// <summary>Key of a region's last brick, inclusive.</summary>
        public static int3 LastKeyOf(int3 regionPos) => FirstKeyOf(regionPos) + (SizeInBricks - 1);

        internal SectorHandle Handle;
        private int3 regionPos;
        private bool attached;

        /// <summary>Position of this region in region units.</summary>
        public int3 RegionPos => regionPos;

        /// <summary>Entity-local block position of this region's first block.</summary>
        public int3 Origin => OriginOf(regionPos);

        /// <summary>True once storage exists behind this region.</summary>
        public bool IsCreated => !Handle.IsNull;

        /// <summary>True once an entity owns the storage; Dispose is then an error.</summary>
        public bool IsAttached => attached;

        /// <summary>Number of allocated bricks in this region. Never allocates.</summary>
        public int AllocatedBrickCount => Handle.IsNull ? 0 : Handle.NonEmptyBrickCount;

        /// <summary>
        /// Allocates a DETACHED region at <paramref name="regionPos"/>. The caller owns the
        /// storage until <see cref="VoxelEntityData.AttachRegion"/> takes it, and must
        /// <see cref="Dispose"/> it otherwise.
        /// </summary>
        /// <remarks>
        /// The allocator must be <see cref="Allocator.Persistent"/>: an entity frees a region's
        /// storage with Persistent when it removes it.
        /// </remarks>
        public static VoxelRegion Create(
            int3 regionPos, int initialBricks = 1, Allocator allocator = Allocator.Persistent)
        {
            CheckPersistent(allocator);
            return new VoxelRegion
            {
                Handle = SectorHandle.AllocEmpty(initialBricks, allocator),
                regionPos = regionPos
            };
        }

        /// <summary>An attached view over storage the entity owns. See <see cref="VoxelEntityData.TryOpenRegion"/>.</summary>
        internal static VoxelRegion Attached(int3 regionPos, SectorHandle handle)
            => new VoxelRegion { Handle = handle, regionPos = regionPos, attached = true };

        /// <summary>Marks this region as owned by an entity, so <see cref="Dispose"/> becomes an error.</summary>
        internal void MarkAttached() => attached = true;

        /// <summary>Frees a DETACHED region. Attached storage belongs to the entity.</summary>
        public void Dispose()
        {
            CheckDetached();
            if (!Handle.IsNull)
            {
                Handle.Dispose(Allocator.Persistent);
            }

            Handle = default;
        }

        /// <summary>
        /// The block at an entity-local <paramref name="blockPos"/> inside this region, or
        /// <c>default</c> when its brick is not allocated. Never allocates.
        /// </summary>
        public Block GetBlock(int3 blockPos) => GetSlot<Block>(SectorSlotId.Block, blockPos);

        /// <summary>
        /// One slot value at an entity-local <paramref name="blockPos"/> inside this region, or
        /// <c>default</c> when its brick or the slot has no storage. Never allocates.
        /// </summary>
        public T GetSlot<T>(SectorSlotId slot, int3 blockPos) where T : unmanaged
        {
            int3 local = Local(blockPos);
            if (Handle.IsNull)
            {
                return default;
            }

            return Handle.GetSlot<T>(slot, local.x, local.y, local.z);
        }

        /// <summary>
        /// Writes a block at an entity-local <paramref name="blockPos"/> inside this region.
        /// Allocates the brick and the slot storage when the value is not <c>default</c>.
        /// Thread-safe per brick.
        /// </summary>
        public void SetBlock(int3 blockPos, Block block) => SetSlot(SectorSlotId.Block, blockPos, block);

        /// <summary>
        /// Writes one slot value at an entity-local <paramref name="blockPos"/> inside this region.
        /// Allocates the brick and the slot storage when the value is not <c>default</c>.
        /// Thread-safe per brick.
        /// </summary>
        public void SetSlot<T>(SectorSlotId slot, int3 blockPos, T value)
            where T : unmanaged, IEquatable<T>
        {
            int3 local = Local(blockPos);
            if (Handle.IsNull)
            {
                CheckCreated();
                return;
            }

            Handle.SetSlot(slot, local.x, local.y, local.z, value);
        }

        private int3 Local(int3 blockPos)
        {
            int3 local = blockPos - Origin;
            CheckInRegion(local);
            return local;
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckInRegion(int3 local)
        {
            if (math.any(local < 0) || math.any(local >= SizeInBlocks))
            {
                throw new IndexOutOfRangeException(
                    "VoxelRegion access outside the region's block box.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckDetached()
        {
            if (attached)
            {
                throw new InvalidOperationException(
                    "This region is attached to an entity; the entity owns its storage.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckCreated()
        {
            if (Handle.IsNull)
            {
                throw new InvalidOperationException(
                    "This region has no storage; create it or open it from an entity first.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckPersistent(Allocator allocator)
        {
            if (allocator != Allocator.Persistent)
            {
                throw new ArgumentException(
                    "A region must be allocated with Allocator.Persistent; the entity frees it with Persistent.",
                    nameof(allocator));
            }
        }
    }
}

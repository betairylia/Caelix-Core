using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>What happened to a brick during one cycle.</summary>
    public enum ChangeKind : byte
    {
        /// <summary>The brick still exists and its contents or its neighbourhood changed.</summary>
        Updated = 0,

        /// <summary>The brick's storage was freed. Captured before the free, because a later scan cannot find it.</summary>
        Removed = 1,
    }

    /// <summary>
    /// One brick of an entity that changed or needs work in the current cycle.
    /// </summary>
    /// <remarks>
    /// One entry per key per cycle. <see cref="DirtyFlags"/> is what changed in THIS brick (the
    /// brick's own dirty flags); <see cref="RequireUpdateFlags"/> is the work the brick needs after
    /// propagation (its require-update flags). One voxel edit at the edge of brick A gives A both
    /// fields and its neighbour B only <see cref="RequireUpdateFlags"/>: replication sends A, the
    /// renderer rebuilds A and B. One flags field cannot express both.
    /// </remarks>
    public struct BrickChange
    {
        /// <summary>Entity-local brick key. See <see cref="BrickKey"/>.</summary>
        public int3 Key;

        /// <summary>Whether the brick was updated or removed.</summary>
        public ChangeKind Kind;

        /// <summary>The brick's own dirty flags: what changed inside it.</summary>
        public BrickUpdateFlags DirtyFlags;

        /// <summary>The brick's require-update flags after propagation: what work it needs.</summary>
        public BrickUpdateFlags RequireUpdateFlags;
    }

    /// <summary>
    /// Automata work record with an entity-local key, produced by
    /// <see cref="VoxelEntityData.CollectRequiredBricks"/>.
    /// </summary>
    /// <remarks>
    /// The key is entity-local, so nothing here leaks the storage layout into game code. It
    /// replaced an older sector-local record that carried a sector handle and a sector-local
    /// brick origin.
    /// </remarks>
    public struct RequiredBrick
    {
        /// <summary>The entity the brick belongs to.</summary>
        public Guid128 Entity;

        /// <summary>Entity-local brick key. See <see cref="BrickKey"/>.</summary>
        public int3 Key;

        /// <summary>The brick's require-update flags after propagation.</summary>
        public BrickUpdateFlags Flags;

        /// <summary>
        /// True when the brick held storage at collection time. False for a position a neighbour
        /// flagged that has no brick yet (only produced when the collector includes empty positions).
        /// </summary>
        public bool IsAllocated;

        /// <summary>The entity's pose at the time the record was collected.</summary>
        public RigidTransform LocalToWorld;
    }
}

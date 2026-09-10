using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Opaque last-lookup cache for hot loops that bind neighbouring brick keys. A
    /// <c>default</c> value is an empty cache, so a caller just declares one and passes it by
    /// reference.
    /// </summary>
    /// <remarks>
    /// Keys that share a region resolve once instead of once per key. An ABSENT region is cached
    /// too, so a sweep across empty space costs one lookup per region, not one per brick. The
    /// cached handle follows the phase rule of <see cref="VoxelEntityData"/>: it is only valid
    /// until the entity's storage is mutated, and nothing re-validates it. Reset it by assigning
    /// <c>default</c>.
    /// </remarks>
    public struct BrickCursor
    {
        /// <summary>Region position the cached handle belongs to. Meaningful only while <see cref="Valid"/>.</summary>
        /// <summary>Storage the cache was filled from; a cursor handed to another entity starts over.</summary>
        internal System.IntPtr Owner;

        internal int3 SectorPos;

        /// <summary>The cached storage handle, null when that region is absent.</summary>
        internal SectorHandle Handle;

        /// <summary>True once a lookup has been cached.</summary>
        internal bool Valid;
    }
}

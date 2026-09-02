using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix
{
    /// <summary>
    /// One brick that needs work this tick, as handed to automata hooks. Built by the server's
    /// brick collector from the require-update flags; consumers read
    /// <see cref="BrickRequireUpdateFlag"/>, never the raw dirty flag.
    /// </summary>
    public struct BrickInfo
    {
        public Guid128 EntityId;
        public int3 SectorPos;
        public int3 BrickOrigin;
        public short BrickId;
        public RigidTransform LocalToWorld;

        public SectorHandle Sector;
        public SectorNeighborHandles Neighbors;

        // Raw source flag; consumers should read BrickRequireUpdateFlag instead (cleared every
        // tick at the end of dirty propagation). Underscore-prefixed to flag the foot-gun.
        public ushort _BrickDirtyFlag;
        public ushort BrickRequireUpdateFlag;
    }
}

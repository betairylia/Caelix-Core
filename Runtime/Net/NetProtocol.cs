using Unity.Mathematics;
using Caelix.Utils;

namespace Caelix.Net
{
    /// <summary>
    /// Wire message kinds. See <c>Documentation~/SERVER_CLIENT_ARCHITECTURE.md</c> section 6.
    /// </summary>
    public enum NetMessageType : byte
    {
        Invalid = 0,

        // Server to client
        Hello = 1,
        EntitySpawn = 2,
        EntityDespawn = 3,
        EntityState = 4,
        EntityTransform = 5,
        SectorAdd = 6,
        SectorRemove = 7,
        BrickData = 8,
        Event = 9,
        QueryReply = 10,

        // Client to server
        Command = 32,
        Query = 33,
    }

    /// <summary>
    /// Fixed envelope at the start of every message: kind, owning world, and the server tick the
    /// message describes (or, for client messages, the last tick the client had seen).
    /// </summary>
    public struct NetHeader
    {
        public NetMessageType Type;
        public ushort WorldId;
        public uint Tick;

        public const int Size = 1 + 2 + 4;

        public static void Write(NetMessageWriter w, NetMessageType type, ushort worldId, uint tick)
        {
            w.Write((byte)type);
            w.Write(worldId);
            w.Write(tick);
        }

        public static NetHeader Read(ref NetMessageReader r)
        {
            return new NetHeader
            {
                Type = (NetMessageType)r.Read<byte>(),
                WorldId = r.Read<ushort>(),
                Tick = r.Read<uint>(),
            };
        }
    }

    public struct HelloMessage
    {
        public ushort ReplicatedSlotMask;
        public float TickRate;
        public uint RegistryHash;
        public ushort WorldCount;
    }

    public struct EntitySpawnMessage
    {
        public Guid128 Guid;
        public RigidTransform Transform;
        public byte IsStatic;
        public byte IsProtected;
        public byte HasBody;
    }

    public struct EntityDespawnMessage
    {
        public Guid128 Guid;
    }

    public struct EntityStateMessage
    {
        public Guid128 Guid;
        public byte IsStatic;
        public byte IsProtected;
        public byte HasBody;
    }

    public struct EntityTransformMessage
    {
        public Guid128 Guid;
        public RigidTransform Transform;
    }

    public struct SectorMessage
    {
        public Guid128 Guid;
        public int3 SectorPos;
    }

    /// <summary>
    /// One sector's changed bricks. Followed by <see cref="BrickCount"/> records of
    /// <c>u16 brickIdx, u16 dirtyFlags</c> then the brick's slot records as written by
    /// <see cref="Sector.WriteReplicatedBrick"/>.
    /// </summary>
    public struct BrickBatchHeader
    {
        public Guid128 Guid;
        public int3 SectorPos;
        public ushort BrickCount;
    }

    /// <summary>Followed by <c>SizeOf(TypeId)</c> payload bytes.</summary>
    public struct TypedPayloadHeader
    {
        public ushort TypeId;
    }

    public struct VoxelQueryMessage
    {
        public uint RequestId;
        public Guid128 Guid;
        public int3 Position;
        public ushort SlotMask;
    }

    /// <summary>
    /// Followed by <c>u8 count</c> then <c>count</c> records of <c>u8 slotId, u16 stride, byte[stride]</c>,
    /// as written by <see cref="Sector.WriteVoxelSlots"/>. Count is zero when the voxel was not found.
    /// </summary>
    public struct VoxelQueryReplyHeader
    {
        public uint RequestId;
        public Guid128 Guid;
        public int3 Position;
        public byte Found;
    }
}

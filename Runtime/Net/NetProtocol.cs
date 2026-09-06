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
        /// <summary>Connection greeting, world id <see cref="NetHeader.NoWorld"/>. See <see cref="HelloMessage"/>.</summary>
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
        TypedQueryReply = 11,
        /// <summary>A world the client must create a replica for. See <see cref="WorldAddMessage"/>.</summary>
        WorldAdd = 12,
        /// <summary>A world the client must drop. See <see cref="WorldRemoveMessage"/>.</summary>
        WorldRemove = 13,

        // Client to server
        Command = 32,
        Query = 33,
        TypedQuery = 34,
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

        /// <summary>
        /// World id of a message that is not scoped to one world, such as Hello. World id 0 is a
        /// valid world (the default world), so the sentinel is the top value. Every dispatcher must
        /// test for NoWorld before it looks a world up; a world-scoped message never carries it.
        /// </summary>
        public const ushort NoWorld = 0xFFFF;

        public static void Write(NetMessageWriter w, NetMessageType type, ushort worldId, uint tick)
        {
            w.Write((byte)type);
            w.Write(worldId);
            w.Write(tick);
        }

        /// <summary>Unmanaged, Burst-usable twin of the <see cref="NetMessageWriter"/> overload.</summary>
        public static void Write(ref UnsafeNetWriter w, NetMessageType type, ushort worldId, uint tick)
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

    /// <summary>
    /// Sent once per connection with header world id <see cref="NetHeader.NoWorld"/>. Worlds follow
    /// as <see cref="NetMessageType.WorldAdd"/> messages.
    /// </summary>
    public struct HelloMessage
    {
        public float TickRate;
        
        // TODO: VibeReview: Maybe replace this with a "Command List" or something for validation?
        // Client only need to be a subset of Server commands.
        public uint RegistryHash;
    }

    /// <summary>
    /// Header world id names the world. Sent before the first replication of that world; the client
    /// creates its replica on receipt.
    /// </summary>
    public struct WorldAddMessage
    {
        public ushort ReplicatedSlotMask;
    }

    /// <summary>
    /// Header world id names the world. The client disposes its replica on receipt.
    /// </summary>
    public struct WorldRemoveMessage
    {
        /// <summary>Padding so the payload is a non-empty struct that <c>Read&lt;T&gt;</c> can read.</summary>
        public byte Reserved;
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

    /// <summary>
    /// Header of a typed query (client to server) and of its reply (server to client). Followed by
    /// the registered struct of <see cref="TypeId"/>, then any trailing payload bytes up to the end
    /// of the message.
    /// </summary>
    public struct TypedQueryHeader
    {
        public ushort TypeId;
        public uint RequestId;
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

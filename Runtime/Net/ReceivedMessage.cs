using System;
using System.Buffers;

namespace Caelix.Net
{
    /// <summary>
    /// Owns received bytes until Dispose. Length is the message size, not the rented array's
    /// capacity. Transfer ownership by clearing the source value; do not dispose copies twice.
    /// </summary>
    public struct ReceivedMessage : IDisposable
    {
        // Cover the server's default 64 MiB replication chunk, including messages above
        // the default pool's size limit on some Unity runtimes. Retain at most two arrays
        // per size bucket; excess queued messages are released when returned.
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(64 * 1024 * 1024, 2);
        public byte[] Buffer { get; private set; }
        public int Length { get; private set; }
        private bool pooled;

        internal ReceivedMessage(byte[] buffer, int length, bool pooled)
        {
            Buffer = buffer;
            Length = length;
            this.pooled = pooled;
        }

        internal static ReceivedMessage Copy(ReadOnlySpan<byte> payload)
        {
            byte[] buffer = Pool.Rent(payload.Length);
            payload.CopyTo(buffer);
            return new ReceivedMessage(buffer, payload.Length, true);
        }

        public void Dispose()
        {
            if (pooled && Buffer != null) Pool.Return(Buffer);
            this = default;
        }
    }

    /// <summary>Optional receive path for channels that can transfer pooled message ownership.</summary>
    public interface ILeasedNetChannel
    {
        bool TryReceiveLease(out ReceivedMessage message);
    }

    public static class NetChannelReceive
    {
        public static bool TryReceiveLease(this INetChannel channel, Predicate<byte[]> predicate, out ReceivedMessage message)
        {
            if (channel.TryReceive(predicate, out byte[] bytes))
            {
                message = new ReceivedMessage(bytes, bytes.Length, false);
                return true;
            }
            message = default;
            return false;
        }

        /// <summary>Uses leased bytes when available; adapts existing channels without changing their contract.</summary>
        public static bool TryReceiveLease(this INetChannel channel, out ReceivedMessage message)
        {
            if (channel is ILeasedNetChannel leased) return leased.TryReceiveLease(out message);
            if (channel.TryReceive(out byte[] bytes))
            {
                message = new ReceivedMessage(bytes, bytes.Length, false);
                return true;
            }
            message = default;
            return false;
        }
    }
}

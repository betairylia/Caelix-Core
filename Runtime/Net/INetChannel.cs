using System;

namespace Caelix.Net
{
    public enum NetDelivery : byte
    {
        /// <summary>Delivered exactly once, in order. Lifecycle, deltas, commands.</summary>
        Reliable = 0,

        /// <summary>May be dropped or reordered. Transform snapshots, where only the newest matters.</summary>
        Unreliable = 1,
    }

    /// <summary>
    /// One end of a bidirectional message channel. Messages are opaque byte blobs framed by the
    /// channel; the payload is copied on send. Implementations: <see cref="LocalChannel"/>
    /// (in-process) and, later, a Unity Transport channel in its own assembly.
    /// </summary>
    public interface INetChannel : IDisposable
    {
        bool IsConnected { get; }

        /// <summary>Queues one message. The span is copied before this returns.</summary>
        void Send(NetDelivery delivery, ReadOnlySpan<byte> payload);

        /// <summary>
        /// Dequeues one received message. The returned array is owned by the caller and is exactly
        /// the message length.
        /// </summary>
        bool TryReceive(out byte[] message);

        /// <summary>
        /// Dequeues the first message accepted by the predicate. Unmatched messages stay queued
        /// in their original order. The predicate must not mutate the message or the channel.
        /// Used to service queries while paused without consuming pending commands.
        /// </summary>
        bool TryReceive_RotateQueue(Predicate<byte[]> predicate, out byte[] message);

        /// <summary>Number of messages waiting in the inbox.</summary>
        int PendingCount { get; }
    }
}

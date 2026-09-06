using System;
using System.Collections.Generic;

namespace Caelix.Net
{
    /// <summary>
    /// In-process channel: a pair of queues. Messages are serialized and copied exactly as they
    /// would be on the wire, so the boundary is real even when server and client share a process.
    /// Not thread-safe; both ends run on the main thread.
    /// </summary>
    public sealed class LocalChannel : INetChannel
    {
        private readonly Queue<byte[]> inbox = new();
        private LocalChannel peer;
        private bool disposed;

        private LocalChannel()
        {
        }

        /// <summary>Creates two connected ends.</summary>
        public static void CreatePair(out LocalChannel a, out LocalChannel b)
        {
            a = new LocalChannel();
            b = new LocalChannel();
            a.peer = b;
            b.peer = a;
        }

        public bool IsConnected => !disposed && peer != null && !peer.disposed;

        public int PendingCount => inbox.Count;

        /// <summary>Payload bytes retained by this inbox, excluding managed object overhead.</summary>
        public long PendingBytes { get; private set; }
        public long PeakPendingBytes { get; private set; }
        public long TotalReceivedBytes { get; private set; }

        public void Send(NetDelivery delivery, ReadOnlySpan<byte> payload)
        {
            if (!IsConnected)
            {
                return;
            }

            // Both delivery modes behave as reliable in-process; the flag only matters on a real
            // transport. Copy so the sender may reuse its writer immediately.
            peer.inbox.Enqueue(payload.ToArray());
            peer.PendingBytes += payload.Length;
            peer.TotalReceivedBytes += payload.Length;
            peer.PeakPendingBytes = Math.Max(peer.PeakPendingBytes, peer.PendingBytes);
        }

        public bool TryReceive(out byte[] message)
        {
            if (inbox.Count > 0)
            {
                message = inbox.Dequeue();
                PendingBytes -= message.Length;
                return true;
            }

            message = null;
            return false;
        }

        /// <summary>
        /// Scans the whole inbox once and returns the first message the predicate accepts. Every
        /// other message stays queued in its original order, so a selective receive never reorders
        /// what it leaves behind. The scan runs to the end even after a match, so the cost is
        /// O(pending) per call; the ordinary <see cref="TryReceive(out byte[])"/> stays O(1).
        /// </summary>
        public bool TryReceive(Predicate<byte[]> predicate, out byte[] message)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            message = null;
            int count = inbox.Count;
            // Complete the rotation even after finding a match, preserving the order of every
            // retained message. Ordinary receive remains O(1).
            for (int i = 0; i < count; i++)
            {
                byte[] candidate = inbox.Dequeue();
                if (message == null && predicate(candidate))
                {
                    message = candidate;
                    PendingBytes -= candidate.Length;
                }
                else
                {
                    inbox.Enqueue(candidate);
                }
            }

            return message != null;
        }

        public void Dispose()
        {
            disposed = true;
            inbox.Clear();
            PendingBytes = 0;
            if (peer != null && peer.peer == this)
            {
                peer.peer = null;
            }
            peer = null;
        }
    }
}

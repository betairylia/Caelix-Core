using System;
using System.Collections.Generic;

namespace Caelix.Net
{
    /// <summary>
    /// In-process channel: a pair of queues. Messages are serialized and copied exactly as they
    /// would be on the wire, so the boundary is real even when server and client share a process.
    /// Not thread-safe; both ends run on the main thread.
    /// </summary>
    public sealed class LocalChannel : INetChannel, ILeasedNetChannel
    {
        private readonly Queue<ReceivedMessage> inbox = new();
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
            peer.inbox.Enqueue(ReceivedMessage.Copy(payload));
            peer.PendingBytes += payload.Length;
            peer.TotalReceivedBytes += payload.Length;
            peer.PeakPendingBytes = Math.Max(peer.PeakPendingBytes, peer.PendingBytes);
        }

        public bool TryReceive(out byte[] message)
        {
            if (!TryReceiveLease(out ReceivedMessage lease))
            {
                message = null;
                return false;
            }
            try { message = lease.Buffer.AsSpan(0, lease.Length).ToArray(); }
            finally { lease.Dispose(); }
            return true;
        }

        /// <summary>Transfers the queued allocation; the receiver returns it after all readers complete.</summary>
        public bool TryReceiveLease(out ReceivedMessage message)
        {
            if (inbox.Count > 0)
            {
                message = inbox.Dequeue();
                PendingBytes -= message.Length;
                return true;
            }

            message = default;
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
                ReceivedMessage candidate = inbox.Dequeue();
                // The legacy predicate API promises an exact-sized caller-owned byte array.
                // Selective receives are used for small server queries, not bulk replication.
                bool accepted = false;
                try
                {
                    if (message == null)
                    {
                        byte[] copy = candidate.Buffer.AsSpan(0, candidate.Length).ToArray();
                        if (predicate(copy)) { message = copy; accepted = true; }
                    }
                }
                finally
                {
                    if (accepted)
                    {
                        PendingBytes -= candidate.Length;
                        candidate.Dispose();
                    }
                    else inbox.Enqueue(candidate);
                }
            }

            return message != null;
        }

        public void Dispose()
        {
            disposed = true;
            while (inbox.Count > 0) { ReceivedMessage message = inbox.Dequeue(); message.Dispose(); }
            PendingBytes = 0;
            if (peer != null && peer.peer == this)
            {
                peer.peer = null;
            }
            peer = null;
        }
    }
}

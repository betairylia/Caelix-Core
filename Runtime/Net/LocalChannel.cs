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

        public void Send(NetDelivery delivery, ReadOnlySpan<byte> payload)
        {
            if (!IsConnected)
            {
                return;
            }

            // Both delivery modes behave as reliable in-process; the flag only matters on a real
            // transport. Copy so the sender may reuse its writer immediately.
            peer.inbox.Enqueue(payload.ToArray());
        }

        public bool TryReceive(out byte[] message)
        {
            if (inbox.Count > 0)
            {
                message = inbox.Dequeue();
                return true;
            }

            message = null;
            return false;
        }

        public void Dispose()
        {
            disposed = true;
            inbox.Clear();
            if (peer != null && peer.peer == this)
            {
                peer.peer = null;
            }
            peer = null;
        }
    }
}

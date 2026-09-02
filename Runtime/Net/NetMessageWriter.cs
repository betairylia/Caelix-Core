using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix.Net
{
    /// <summary>
    /// Growable byte buffer for building one outgoing message. Reused across messages: call
    /// <see cref="Reset"/>, write, then hand <see cref="AsSpan"/> to the channel, which copies.
    /// Values are written in memory layout (little-endian on every supported platform).
    /// </summary>
    public sealed unsafe class NetMessageWriter
    {
        private byte[] buffer;
        private int length;

        public NetMessageWriter(int initialCapacity = 1024)
        {
            buffer = new byte[Math.Max(16, initialCapacity)];
            length = 0;
        }

        public int Length => length;
        public byte[] Buffer => buffer;
        public ReadOnlySpan<byte> AsSpan() => new ReadOnlySpan<byte>(buffer, 0, length);

        public void Reset()
        {
            length = 0;
        }

        public void Write<T>(T value) where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            EnsureCapacity(length + size);
            fixed (byte* dst = buffer)
            {
                UnsafeUtility.CopyStructureToPtr(ref value, dst + length);
            }
            length += size;
        }

        public void WriteBytes(void* src, int count)
        {
            if (count <= 0) return;
            EnsureCapacity(length + count);
            fixed (byte* dst = buffer)
            {
                UnsafeUtility.MemCpy(dst + length, src, count);
            }
            length += count;
        }

        public void WriteBytes(ReadOnlySpan<byte> src)
        {
            if (src.Length == 0) return;
            EnsureCapacity(length + src.Length);
            src.CopyTo(new Span<byte>(buffer, length, src.Length));
            length += src.Length;
        }

        /// <summary>
        /// Reserves <paramref name="count"/> bytes and returns their offset so a caller can patch
        /// them later with <see cref="Patch{T}"/> (used for counts that are only known after the
        /// records that follow them have been written).
        /// </summary>
        public int Reserve<T>() where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            int offset = length;
            EnsureCapacity(length + size);
            length += size;
            return offset;
        }

        public void Patch<T>(int offset, T value) where T : unmanaged
        {
            fixed (byte* dst = buffer)
            {
                UnsafeUtility.CopyStructureToPtr(ref value, dst + offset);
            }
        }

        private void EnsureCapacity(int required)
        {
            if (required <= buffer.Length) return;
            int newSize = buffer.Length * 2;
            while (newSize < required) newSize *= 2;
            Array.Resize(ref buffer, newSize);
        }
    }
}

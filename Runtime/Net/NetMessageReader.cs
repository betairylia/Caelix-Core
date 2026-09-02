using System;
using System.IO;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix.Net
{
    /// <summary>
    /// Sequential reader over one received message. Mirrors <see cref="NetMessageWriter"/>.
    /// Reads past the end throw <see cref="EndOfStreamException"/>, which callers treat as a
    /// malformed message.
    /// </summary>
    public unsafe struct NetMessageReader
    {
        private readonly byte[] buffer;
        private readonly int length;
        private int position;

        public NetMessageReader(byte[] buffer, int length)
        {
            this.buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            this.length = length;
            position = 0;
        }

        public NetMessageReader(byte[] buffer) : this(buffer, buffer?.Length ?? 0)
        {
        }

        public int Position => position;
        public int Remaining => length - position;

        public T Read<T>() where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            Require(size);
            T value;
            fixed (byte* src = buffer)
            {
                UnsafeUtility.CopyPtrToStructure(src + position, out value);
            }
            position += size;
            return value;
        }

        public void ReadBytes(void* dst, int count)
        {
            if (count <= 0) return;
            Require(count);
            fixed (byte* src = buffer)
            {
                UnsafeUtility.MemCpy(dst, src + position, count);
            }
            position += count;
        }

        public ReadOnlySpan<byte> ReadSpan(int count)
        {
            Require(count);
            var span = new ReadOnlySpan<byte>(buffer, position, count);
            position += count;
            return span;
        }

        public void Skip(int count)
        {
            Require(count);
            position += count;
        }

        private void Require(int count)
        {
            if (count < 0 || position + count > length)
            {
                throw new EndOfStreamException(
                    $"Net message truncated: needed {count} bytes at offset {position}, message has {length}.");
            }
        }
    }
}

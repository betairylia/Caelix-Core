using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix.Net
{
    /// <summary>
    /// Fixed-capacity, unmanaged message writer over caller-owned memory. Usable inside Burst jobs.
    /// Mirrors <see cref="NetMessageWriter"/>'s API; it never grows, so the caller must size the
    /// buffer first (see <see cref="Sector.ReplicatedBrickBytes"/>). Overrun throws under
    /// ENABLE_UNITY_COLLECTIONS_CHECKS and is otherwise unchecked.
    /// </summary>
    /// <remarks>
    /// The writer does not own <see cref="Ptr"/>: it neither allocates nor frees it, and it holds no
    /// safety handle. The caller keeps the memory alive for the writer's whole lifetime. Values are
    /// written in memory layout (little-endian on every supported platform), byte for byte identical
    /// to what <see cref="NetMessageWriter"/> produces, so either side of the wire may use either
    /// writer.
    /// </remarks>
    public unsafe struct UnsafeNetWriter
    {
        [NativeDisableUnsafePtrRestriction]
        private byte* buffer;
        private int capacity;
        private int length;

        /// <summary>
        /// Wraps <paramref name="capacity"/> bytes at <paramref name="buffer"/>, which the caller
        /// owns and keeps alive.
        /// </summary>
        public UnsafeNetWriter(byte* buffer, int capacity)
        {
            this.buffer = buffer;
            this.capacity = capacity;
            length = 0;
        }

        /// <summary>Bytes written so far.</summary>
        public int Length => length;

        /// <summary>Bytes the buffer can hold in total.</summary>
        public int Capacity => capacity;

        /// <summary>The base of the caller-owned buffer.</summary>
        public byte* Ptr => buffer;

        /// <summary>Resets the cursor to the start of the buffer; the bytes are not cleared.</summary>
        public void Reset()
        {
            length = 0;
        }

        public void Write<T>(T value) where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            CheckCapacity(size);
            UnsafeUtility.CopyStructureToPtr(ref value, buffer + length);
            length += size;
        }

        public void WriteBytes(void* src, int count)
        {
            if (count <= 0) return;
            CheckCapacity(count);
            UnsafeUtility.MemCpy(buffer + length, src, count);
            length += count;
        }

        /// <summary>
        /// Reserves room for a <typeparamref name="T"/> and returns its offset so a caller can patch
        /// it later with <see cref="Patch{T}"/> (used for counts that are only known after the
        /// records that follow them have been written). The reserved bytes are left as they were.
        /// </summary>
        public int Reserve<T>() where T : unmanaged
        {
            int size = UnsafeUtility.SizeOf<T>();
            CheckCapacity(size);
            int offset = length;
            length += size;
            return offset;
        }

        public void Patch<T>(int offset, T value) where T : unmanaged
        {
            CheckPatchRange(offset, UnsafeUtility.SizeOf<T>());
            UnsafeUtility.CopyStructureToPtr(ref value, buffer + offset);
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckCapacity(int size)
        {
            if (length + size > capacity)
            {
                throw new InvalidOperationException(
                    "UnsafeNetWriter overrun: the buffer cannot hold this write.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckPatchRange(int offset, int size)
        {
            if (offset < 0 || offset + size > length)
            {
                throw new InvalidOperationException(
                    "UnsafeNetWriter patch target lies outside the bytes written so far.");
            }
        }
    }
}

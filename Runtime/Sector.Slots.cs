using System;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// Backing storage for one sector slot. Every slot is strictly per-voxel: <see cref="data"/>
    /// holds one <see cref="stride"/>-byte element for each of a brick's
    /// <see cref="Sector.BLOCKS_IN_BRICK"/> voxels, indexed through the shared brick-id namespace.
    ///
    /// A slot may optionally carry an <see cref="aux"/> buffer — one <see cref="extraPerBrickBytes"/>
    /// record per brick — holding data <em>derived</em> from the per-voxel contents (non-empty
    /// occupancy bitmaps, physics-key masks, …). Aux lives in its own allocation so it can be
    /// attached, grown, cleared and dropped independently of the voxel data. It is never
    /// serialized: it is recomputed from <see cref="data"/> for dirty bricks once a tick's edits
    /// settle.
    /// </summary>
    internal unsafe struct SectorSlotStorage
    {
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> data;   // per-voxel: brickCapacity * BLOCKS_IN_BRICK * stride bytes
        [NativeDisableUnsafePtrRestriction]
        public UnsafeList<byte> aux;    // optional per-brick: brickCapacity * extraPerBrickBytes bytes
        public int stride;              // bytes per voxel element
        public int extraPerBrickBytes;  // bytes per brick in aux; meaningful only while HasAux

        /// <summary>Bytes occupied by one brick's per-voxel records.</summary>
        public int BytesPerBrick => stride * Sector.BLOCKS_IN_BRICK;

        /// <summary>True once the per-voxel buffer exists. This is what "the slot exists" means.</summary>
        public bool IsCreated => data.IsCreated;

        /// <summary>True when this slot carries a derived per-brick aux buffer.</summary>
        public bool HasAux => aux.IsCreated;

        public static SectorSlotStorage New(
            int stride, int initialBricks, Allocator allocator,
            NativeArrayOptions initialization = NativeArrayOptions.ClearMemory,
            int extraPerBrickBytes = 0)
        {
            int voxelBytes = math.max(1, initialBricks * Sector.BLOCKS_IN_BRICK * stride);
            var storage = new SectorSlotStorage
            {
                data = new UnsafeList<byte>(voxelBytes, allocator),
                aux = default,
                stride = stride,
                extraPerBrickBytes = 0
            };
            storage.data.Resize(voxelBytes, initialization);

            if (extraPerBrickBytes > 0)
            {
                storage.AttachAux(extraPerBrickBytes, initialBricks, allocator, initialization);
            }
            return storage;
        }

        /// <summary>
        /// Allocates (replacing any existing) the aux buffer for this slot, sized to hold
        /// <paramref name="brickCapacity"/> bricks. The per-voxel <see cref="data"/> buffer is
        /// expected to already exist — aux summarizes it.
        /// </summary>
        public void AttachAux(
            int extraPerBrickBytes, int brickCapacity, Allocator allocator,
            NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            if (aux.IsCreated) aux.Dispose();
            this.extraPerBrickBytes = extraPerBrickBytes;

            int auxBytes = math.max(1, brickCapacity * extraPerBrickBytes);
            aux = new UnsafeList<byte>(auxBytes, allocator);
            aux.Resize(auxBytes, initialization);
        }

        /// <summary>
        /// Deep-copies this slot, both the voxel <see cref="data"/> and the derived <see cref="aux"/>
        /// buffer. Aux travels with its data so that after a snapshot swap a brick whose data did not
        /// change still has a valid mask; only the bricks that actually changed need re-marking.
        /// </summary>
        public SectorSlotStorage Clone(Allocator allocator)
        {
            SectorSlotStorage clone = default;
            clone.CopyFrom(in this, allocator);
            return clone;
        }

        /// <summary>Copies into independently owned storage, retaining capacity between snapshot swaps.</summary>
        public void CopyFrom(in SectorSlotStorage source, Allocator allocator)
        {
            CopyBuffer(in source.data, ref data, allocator);
            CopyBuffer(in source.aux, ref aux, allocator);
            stride = source.stride;
            extraPerBrickBytes = source.extraPerBrickBytes;
        }

        private static void CopyBuffer(in UnsafeList<byte> source, ref UnsafeList<byte> target, Allocator allocator)
        {
            if (!source.IsCreated)
            {
                if (target.IsCreated) target.Dispose();
                target = default;
                return;
            }

            if (!target.IsCreated) target = new UnsafeList<byte>(source.Length, allocator);
            target.Resize(source.Length, NativeArrayOptions.UninitializedMemory);
            if (source.Length > 0) UnsafeUtility.MemCpy(target.Ptr, source.Ptr, source.Length);
        }

        public void Dispose()
        {
            if (data.IsCreated) data.Dispose();
            if (aux.IsCreated) aux.Dispose();
            data = default;
            aux = default;
            stride = 0;
            extraPerBrickBytes = 0;
        }

        public void EnsureBrickCapacity(int brickCapacity, NativeArrayOptions initialization = NativeArrayOptions.ClearMemory)
        {
            int bytes = brickCapacity * BytesPerBrick;
            if (bytes > data.Length) { data.Resize(bytes, initialization); }

            if (aux.IsCreated)
            {
                int auxBytes = brickCapacity * extraPerBrickBytes;
                if (auxBytes > aux.Length) { aux.Resize(auxBytes, initialization); }
            }
        }

        public void ClearBrick(short bid)
        {
            UnsafeUtility.MemClear(data.Ptr + bid * BytesPerBrick, BytesPerBrick);
            if (aux.IsCreated)
            {
                UnsafeUtility.MemClear(aux.Ptr + bid * extraPerBrickBytes, extraPerBrickBytes);
            }
        }

        // The element size is taken as a parameter rather than read from the `stride` field so that
        // callers with a statically-known element type can pass a compile-time constant
        // (UnsafeUtility.SizeOf<T>(), or a literal): under Burst that folds the `* inStride` to a
        // shift and drops the `this.stride` load from the hot per-voxel path. The brick shift is
        // already constant via BLOCKS_IN_BRICK, so only the stride needs threading.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get<T>(short bid, int voxelIdxInBrick, int inStride) where T : unmanaged
        {
            if (!IsCreated) return default;

            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * inStride;
            return Get<T>(byteOffset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Get<T>(int byteOffset) where T : unmanaged
        {
            return UnsafeUtility.ReadArrayElement<T>(data.Ptr + byteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(short bid, int voxelIdxInBrick, int inStride, T value) where T : unmanaged
        {
            int byteOffset = (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * inStride;
            Set<T>(byteOffset, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Set<T>(int byteOffset, T value) where T : unmanaged
        {
            UnsafeUtility.WriteArrayElement(data.Ptr + byteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T GetAux<T>(int auxByteOffset) where T : unmanaged
        {
            return UnsafeUtility.ReadArrayElement<T>(aux.Ptr + auxByteOffset, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetAux<T>(int auxByteOffset, T value) where T : unmanaged
        {
            UnsafeUtility.WriteArrayElement(aux.Ptr + auxByteOffset, 0, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickPtr(short bid)
        {
            return data.Ptr + bid * BytesPerBrick;
        }

        /// <summary>
        /// Pointer to one voxel's <see cref="stride"/>-byte record inside brick <paramref name="bid"/>.
        /// The single place that knows how a voxel is addressed inside a brick, so callers that copy
        /// raw bytes (the replication codec, the voxel query reply) do not hand-roll the arithmetic.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte* GetVoxelPtr(short bid, int voxelIdxInBrick)
        {
            return data.Ptr + (bid * Sector.BLOCKS_IN_BRICK + voxelIdxInBrick) * stride;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickAuxPtr(short bid)
        {
            return aux.Ptr + bid * extraPerBrickBytes;
        }
    }

    internal unsafe partial struct Sector
    {
        /// <summary>
        /// Fixed table of enum-addressed slot storage. Slot data uses the shared <see cref="brickMap"/>
        /// namespace. If a shared brick ID exists, every created slot has data for it.
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public SectorSlotStorage* slots;

        /// <summary>
        /// Backbuffer version of slot storage for read access during double-buffered updates (e.g., cellular automata).
        /// </summary>
        [NativeDisableUnsafePtrRestriction]
        public SectorSlotStorage* _snapshot_slots;

        private static void ExtendCreatedSlots(SectorSlotStorage* slotTable, int brickCapacity)
        {
            // if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->EnsureBrickCapacity(brickCapacity); }
            }
        }

        private static void ClearBrickForAllSlots(SectorSlotStorage* slotTable, short bid)
        {
            // if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->ClearBrick(bid); }
            }
        }

        /// <summary>
        /// Allocates the brick at <paramref name="brickIdx"/> in <paramref name="brickMap"/> and
        /// makes every created slot in <paramref name="slotTable"/> hold storage for it: the slot
        /// buffers grow when the id table outgrew its capacity, otherwise the reused brick slice is
        /// cleared. Returns the new shared brick id.
        /// </summary>
        /// <remarks>
        /// Precondition: <c>brickMap.indices[brickIdx] == BRICKID_EMPTY</c>. This helper takes no
        /// lock and marks nothing dirty — callers keep their own allocation gate and their own
        /// <c>MarkBrickDirty</c> call, because those differ per caller (the voxel writer runs under a
        /// spin gate and only flags the Block slot, the replication apply exclusively owns the sector and
        /// flags the replica's own bits). Works on whichever tables the caller passes, so the
        /// snapshot backbuffer and the live tables share one implementation.
        /// </remarks>
        private static short AllocateBrick(
            ref SparseBrickIdTable brickMap, SectorSlotStorage* slotTable, int brickIdx, int3 brickPos)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (brickMap.indices[brickIdx] != BRICKID_EMPTY)
            {
                throw new InvalidOperationException("AllocateBrick called for an already allocated brick.");
            }
#endif

            brickMap.AddBrick(brickPos, out int newId, out bool exceedsCapacity);

            if (exceedsCapacity)
                ExtendCreatedSlots(slotTable, brickMap.Capacity);
            else
                ClearBrickForAllSlots(slotTable, (short)newId); // TODO: Do we really need to clear it?

            return (short)newId;
        }

        /// <summary>
        /// Makes slot <paramref name="slotIndex"/> of <paramref name="slotTable"/> exist with the
        /// given per-voxel <paramref name="stride"/> and room for <paramref name="brickCapacity"/>
        /// bricks, attaching a derived aux buffer when <paramref name="extraPerBrickBytes"/> is
        /// positive. Returns the slot pointer. Safe to call repeatedly; only allocates or grows when
        /// needed.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The slot already exists with a different stride. Always thrown, not only under collections
        /// checks: a mismatched stride off the wire is a protocol error and a mismatched stride from
        /// code is a programming error, and both must surface.
        /// </exception>
        private static SectorSlotStorage* EnsureSlotStorage(
            SectorSlotStorage* slotTable, int slotIndex, int stride, int brickCapacity,
            Allocator allocator, int extraPerBrickBytes)
        {
            SectorSlotStorage* slot = slotTable + slotIndex;
            if (!slot->IsCreated)
            {
                *slot = SectorSlotStorage.New(
                    stride, brickCapacity, allocator, NativeArrayOptions.ClearMemory, extraPerBrickBytes);
                return slot;
            }

            if (slot->stride != stride)
            {
                // The interpolated message only exists on the managed path: Burst cannot compile
                // string.Format, and this runs inside Burst jobs through SetVoxelSlot. Under Burst
                // the helper is discarded and the constant-message throw below fires instead, so the
                // mismatch always surfaces either way.
                ThrowSlotStrideMismatch(slotIndex, stride, slot->stride);
                throw new InvalidOperationException("Sector slot stride does not match its storage stride.");
            }

            slot->EnsureBrickCapacity(brickCapacity);
            if (extraPerBrickBytes > 0 && !slot->HasAux)
            {
                slot->AttachAux(extraPerBrickBytes, brickCapacity, allocator);
            }

            return slot;
        }

        [Unity.Burst.BurstDiscard]
        private static void ThrowSlotStrideMismatch(int slotIndex, int stride, int storageStride)
        {
            throw new InvalidOperationException(
                $"Slot {slotIndex} stride {stride} does not match its storage stride {storageStride}.");
        }

        internal static void CopySlotsTo(SectorSlotStorage* from, SectorSlotStorage* to, Allocator allocator)
        {
            // Both tables own their buffers. Carry aux with the voxels so unchanged bricks keep
            // valid masks, while reusing the backbuffer allocations from the preceding swap.
            for (int i = 0; i < MAX_SLOTS; i++) { to[i].CopyFrom(in from[i], allocator); }
        }

        private static void ResetSlotTable(ref SectorSlotStorage* slotTable)
        {
            if (slotTable == null) return;

            for (int i = 0; i < MAX_SLOTS; i++)
            {
                SectorSlotStorage* slot = slotTable + i;
                if (slot->IsCreated) { slot->Dispose(); }
                *slot = default;
            }
        }

        // ---- Per-voxel slot access -------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Block GetBlock(int x, int y, int z)
            => GetVoxelSlot<Block>(SectorSlotId.Block, x, y, z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z) where T : unmanaged
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated) { return default; }

            return slot->Get<T>(bid, ToBlockIdx(x & BRICK_MASK, y & BRICK_MASK, z & BRICK_MASK), UnsafeUtility.SizeOf<T>());
        }

        // ---- Per-brick aux access --------------------------------------------------------------
        // Aux is companion storage on a per-voxel slot. It is always addressed on the live `slots`
        // table (never the snapshot backbuffer) since it is rebuilt from settled voxel data.

        /// <summary>
        /// Ensures the per-voxel storage for <paramref name="slotId"/> exists and can hold every
        /// currently allocated brick, optionally attaching a derived aux buffer of
        /// <paramref name="extraPerBrickBytes"/> bytes per brick. Uses <c>sizeof(T)</c> as the voxel
        /// stride. Safe to call repeatedly; only allocates or grows when needed. Intended for bulk
        /// slot producers (e.g. physics-info generation) that write directly into slot storage. An
        /// existing slot whose stride differs from <c>sizeof(T)</c> throws.
        /// </summary>
        public void EnsureSlotAllocated<T>(SectorSlotId slotId, int extraPerBrickBytes = 0) where T : unmanaged
            => EnsureSlotAllocated(slotId, UnsafeUtility.SizeOf<T>(), extraPerBrickBytes);

        /// <summary>
        /// Untyped core of <see cref="EnsureSlotAllocated{T}"/>: ensures the per-voxel storage for
        /// <paramref name="slotId"/> exists with a <paramref name="stride"/>-byte element and can
        /// hold every currently allocated brick, optionally attaching a derived aux buffer of
        /// <paramref name="extraPerBrickBytes"/> bytes per brick. For callers that learn the stride
        /// at run time (a replicated slot record names its own stride) instead of from a C# type.
        /// An existing slot whose stride differs throws.
        /// </summary>
        public void EnsureSlotAllocated(SectorSlotId slotId, int stride, int extraPerBrickBytes = 0)
        {
            EnsureSlotStorage(slots, (int)slotId, stride, brickMap.Capacity, _allocator, extraPerBrickBytes);
        }

        /// <summary>
        /// Ensures the derived aux buffer for an already-created voxel slot exists and can hold every
        /// currently allocated brick. No-op (or throws under collections checks) if the voxel slot
        /// itself has not been created — aux summarizes voxel data, so that must come first.
        /// </summary>
        public void EnsureAuxAllocated(SectorSlotId slotId, int extraPerBrickBytes)
        {
            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->IsCreated)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Cannot attach aux to a slot whose voxel data is not created; create the slot first.");
#else
                return;
#endif
            }

            slot->EnsureBrickCapacity(brickMap.Capacity);
            if (!slot->HasAux)
            {
                slot->AttachAux(extraPerBrickBytes, brickMap.Capacity, _allocator);
            }
        }

        /// <summary>
        /// Reads a value from a brick's aux slice at <paramref name="offsetInBytes"/>. Returns
        /// default when the brick is empty or the slot carries no aux.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetBrickAux<T>(SectorSlotId slotId, int bX, int bY, int bZ, int offsetInBytes)
            where T : unmanaged
        {
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
                return default;
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->HasAux) { return default; }

            return slot->GetAux<T>(bid * slot->extraPerBrickBytes + offsetInBytes);
        }

        /// <summary>
        /// Raw pointer to a brick's aux slice, or null when the slot carries no aux. Intended for
        /// bulk rebuild producers that fill a brick's whole slice at once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickAuxPtr(SectorSlotId slotId, short bid)
        {
            if (bid == BRICKID_EMPTY) { return null; }
            SectorSlotStorage* slot = slots + (int)slotId;
            return slot->HasAux ? slot->GetBrickAuxPtr(bid) : null;
        }

        /// <summary>
        /// Raw pointer to the aux slice of the brick that CONTAINS the given block coordinates, or
        /// null when the brick is not allocated or the slot carries no aux. Block-coordinate sibling
        /// of <see cref="GetBrickAuxPtr(SectorSlotId, short)"/>, for bulk consumers that walk a voxel
        /// neighborhood instead of a brick list.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void* GetBrickAuxPtrAtBlock(SectorSlotId slotId, int x, int y, int z)
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            return GetBrickAuxPtr(slotId, brickMap.indices[brickIdx]);
        }

        /// <summary>
        /// Raw pointer to the voxel storage of the brick that CONTAINS the given block coordinates,
        /// or null when the brick is not allocated or the slot has no storage.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T* GetBrickPtrAtBlock<T>(SectorSlotId slotId, int x, int y, int z)
            where T : unmanaged, IEquatable<T>
        {
            int brickIdx = ToBrickIdx(x >> SHIFT_IN_BLOCKS, y >> SHIFT_IN_BLOCKS, z >> SHIFT_IN_BLOCKS);
            return GetBrick<T>(slotId, brickMap.indices[brickIdx]);
        }

        /// <summary>
        /// Writes a value into a brick's aux slice at <paramref name="offsetInBytes"/>. The aux
        /// buffer must already exist (see <see cref="EnsureAuxAllocated"/>); unlike the voxel setters
        /// this never lazily allocates and does NOT mark the brick dirty.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Under collections checks, thrown when the brick is not allocated or the slot has no aux.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBrickAux<T>(SectorSlotId slotId, int bX, int bY, int bZ, int offsetInBytes, T value)
            where T : unmanaged
        {
            int brickIdx = ToBrickIdx(bX, bY, bZ);
            short bid = brickMap.indices[brickIdx];
            if (bid == BRICKID_EMPTY)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException("Cannot set brick aux for non-allocated bricks");
#else
                return;
#endif
            }

            SectorSlotStorage* slot = slots + (int)slotId;
            if (!slot->HasAux)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Aux buffer not allocated for this slot; call EnsureAuxAllocated first.");
#else
                return;
#endif
            }

            slot->SetAux<T>(bid * slot->extraPerBrickBytes + offsetInBytes, value);
        }

        // ---- Per-voxel writes ------------------------------------------------------------------
        //
        // Two setter bodies share the storage work (TryPrepareWrite / CommitWrite) and differ only
        // in where the simulation bits come from:
        //   - the DEFAULT body asks AutomataDefaults (the application's block / slot tables);
        //   - the EXPLICIT body takes the caller's mask and never touches those tables.
        // Overload resolution picks the body at compile time, so an explicit write cannot fall into
        // the classifier by accident. Engine bookkeeping (geometry, allocation) is added by the
        // engine in both bodies and cannot be suppressed by a caller mask.

        /// <summary>Writes a block. Simulation bits come from <see cref="AutomataDefaults"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block)
            => SetVoxelSlot(SectorSlotId.Block, x, y, z, block);

        /// <summary>Writes a block raising exactly the given simulation bits (plus engine bookkeeping).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBlock(int x, int y, int z, Block block, BrickUpdateFlags simulationFlags)
            => SetVoxelSlot(SectorSlotId.Block, x, y, z, block, simulationFlags);

        /// <summary>
        /// Writes one slot value. Simulation bits come from <see cref="AutomataDefaults"/>: the
        /// block table (previous and replacement id) for the Block slot, the slot table otherwise.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z, T value)
            where T : unmanaged, IEquatable<T>
        {
            if (!TryPrepareWrite(slotId, x, y, z, value, out VoxelWrite write, out T previous))
            {
                return;
            }

            ushort simulation = slotId == SectorSlotId.Block
                ? AutomataDefaults.BlockWriteFlags(BlockIdOf(ref previous), BlockIdOf(ref value))
                : AutomataDefaults.SlotWriteFlags((int)slotId);

            CommitWrite(slotId, in write, x, y, z, value, (ushort)(MandatoryFlags(slotId) | simulation));
        }

        /// <summary>
        /// Writes one slot value raising exactly <paramref name="simulationFlags"/> (masked to
        /// <see cref="BrickUpdateFlags.AutomataMask"/>) plus engine bookkeeping. Never consults
        /// <see cref="AutomataDefaults"/>. An unchanged value still writes nothing; use the
        /// automata brick's <c>SetDirty</c> for a scheduling request without a data change.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetVoxelSlot<T>(SectorSlotId slotId, int x, int y, int z, T value, BrickUpdateFlags simulationFlags)
            where T : unmanaged, IEquatable<T>
        {
            CheckSimulationMask(simulationFlags);
            if (!TryPrepareWrite(slotId, x, y, z, value, out VoxelWrite write, out T _))
            {
                return;
            }

            ushort simulation = (ushort)(simulationFlags & BrickUpdateFlags.AutomataMask);
            CommitWrite(slotId, in write, x, y, z, value, (ushort)(MandatoryFlags(slotId) | simulation));
        }

        /// <summary>
        /// Where a prepared write lands. Holds the slot-table entry (stable: the table itself never
        /// moves) and indices, never a voxel pointer, because the slot's buffer can be reallocated
        /// between preparing and committing by another brick's allocation.
        /// </summary>
        private struct VoxelWrite
        {
            public SectorSlotStorage* Slot;
            public int BrickIdx;
            public int VoxelIdx;
            public short Bid;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort MandatoryFlags(SectorSlotId slotId)
        {
            return slotId == SectorSlotId.Block
                ? (ushort)DirtyPropagationSettings.BlockWriteGeometryFlags
                : (ushort)0;
        }

        /// <summary>The block id behind a Block-slot value. Only called when <c>slotId == Block</c>, where <typeparamref name="T"/> is <see cref="Block"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort BlockIdOf<T>(ref T value) where T : unmanaged
        {
            CheckBlockSized<T>();
            return UnsafeUtility.As<T, Block>(ref value).id;
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckBlockSized<T>() where T : unmanaged
        {
            if (UnsafeUtility.SizeOf<T>() != UnsafeUtility.SizeOf<Block>())
            {
                throw new InvalidOperationException("The Block slot takes Block-sized values only.");
            }
        }

        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private static void CheckSimulationMask(BrickUpdateFlags simulationFlags)
        {
            if ((simulationFlags & ~BrickUpdateFlags.AutomataMask) != 0)
            {
                throw new ArgumentException(
                    "Only simulation bits (AutomataMask) may be passed to a voxel setter; engine bookkeeping bits are engine-owned.");
            }
        }

        /// <summary>
        /// Resolves the voxel, allocates the brick and the slot storage when needed, and reads the
        /// previous value. Returns false when nothing must be written: writing the default into an
        /// unallocated brick, or writing the value already there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryPrepareWrite<T>(
            SectorSlotId slotId, int x, int y, int z, T value, out VoxelWrite write, out T previous)
            where T : unmanaged, IEquatable<T>
        {
            ref SparseBrickIdTable targetBrickMap = ref(_snapshot_enabled ? ref _snapshot_brickMap : ref brickMap);
            SectorSlotStorage* targetSlots = _snapshot_enabled ? _snapshot_slots : slots;

            int bx = x >> SHIFT_IN_BLOCKS;
            int by = y >> SHIFT_IN_BLOCKS;
            int bz = z >> SHIFT_IN_BLOCKS;
            ResolveVoxel(x, y, z, out int brickIdx, out int voxelIdx);
            short bid = targetBrickMap.indices[brickIdx];

            write = default;
            previous = default;

            // Brick does not exist, need alloc
            if (bid == BRICKID_EMPTY)
            {
                if (value.Equals(default)) return false;

                // Lock and alloc
                AcquireSpinGate(ref _sectorAllocLock);

                // Re-check under the gate; another thread may have created this brick already.
                if (targetBrickMap.indices[brickIdx] == BRICKID_EMPTY)
                {
                    AllocateBrick(ref targetBrickMap, targetSlots, brickIdx, new int3(bx, by, bz));

                    // Record the addition for Block slot
                    if (slotId == SectorSlotId.Block)
                    {
                        MarkBrickDirty(brickIdx, BrickUpdateFlags.BlockBrickAdded, 0);
                    }
                }

                // Pick up the brick id whether we or another thread created it.
                bid = targetBrickMap.indices[brickIdx];

                // Release the lock
                ReleaseSpinGate(ref _sectorAllocLock);
            }

            // Brick exists, corresponding slot may not be allocated yet

            SectorSlotStorage* slot = targetSlots + (int)slotId;

            // Check for same value
            // If slot !IsCreated, Get will return default
            previous = slot->Get<T>(bid, voxelIdx, UnsafeUtility.SizeOf<T>());
            if (value.Equals(previous))
            {
                return false;
            }

            // Allocate the slot if not yet allocated for this sector
            if (!slot->IsCreated)
            {
                AcquireSpinGate(ref _sectorAllocLock);

                if (!slot->IsCreated)
                {
                    EnsureSlotStorage(
                        targetSlots, (int)slotId, UnsafeUtility.SizeOf<T>(),
                        targetBrickMap.Capacity, _allocator, 0);
                }

                ReleaseSpinGate(ref _sectorAllocLock);
            }

            write.Slot = slot;
            write.BrickIdx = brickIdx;
            write.VoxelIdx = voxelIdx;
            write.Bid = bid;
            return true;
        }

        /// <summary>Marks the brick dirty with the final flags and stores the value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CommitWrite<T>(
            SectorSlotId slotId, in VoxelWrite write, int x, int y, int z, T value, ushort dirtyFlags)
            where T : unmanaged, IEquatable<T>
        {
            MarkBrickDirty(write.BrickIdx, (BrickUpdateFlags)dirtyFlags, GetVoxelPropagationMask(write.VoxelIdx));

            // Update AABB
            // TODO: Rescan AABB occasionally to handle block removal
            // TODO: Make AABB tight with current snapshot logic -- current version is correct (always superset) but may not be ideal
            if (slotId == SectorSlotId.Block && !value.Equals(default))
                blockAABB.Update(new int3(x, y, z));

            write.Slot->Set(write.Bid, write.VoxelIdx, UnsafeUtility.SizeOf<T>(), value);
        }

        /// <summary>
        /// Raises simulation bits on a brick's source dirty state without changing any voxel: an
        /// automaton's request to run again next tick. Whole-brick propagation (no changed voxel to
        /// narrow the direction mask from); no geometry or allocation bookkeeping.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkBrickSimulationDirty(int brickIdx, BrickUpdateFlags simulationFlags)
        {
            CheckSimulationMask(simulationFlags);
            ushort masked = (ushort)(simulationFlags & BrickUpdateFlags.AutomataMask);
            if (masked == 0) return;
            MarkBrickDirty(brickIdx, (BrickUpdateFlags)masked, 0xFFFFFFFF);
        }
    }
}

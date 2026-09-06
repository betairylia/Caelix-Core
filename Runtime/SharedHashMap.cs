using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix
{
    /// <summary>
    /// Hash map with handle semantics: the map lives in one heap allocation and every copy of
    /// this struct points at it, so a mutation through any copy is visible through all of them.
    /// </summary>
    /// <remarks>
    /// <see cref="UnsafeHashMap{TKey,TValue}"/> embeds its hash helper by value. A copy of a
    /// plain UnsafeHashMap keeps its own count, free index, and buffer pointer, so adding through
    /// one copy leaves every other copy with a stale header, and a resize leaves them pointing
    /// at freed memory. <see cref="VoxelEntityData"/> is copied freely (world store, views,
    /// jobs), so its maps must alias. The pointer indirection here is what makes that safe.
    /// Mark job fields <c>[ReadOnly]</c> to document that a job must not mutate the map.
    /// A default instance is not created; every mutating or enumerating member throws under
    /// collections checks. Dispose through one copy leaves every other copy pointing at freed
    /// memory, so only the owner disposes.
    /// </remarks>
    public unsafe struct SharedHashMap<TKey, TValue> : IDisposable
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        private UnsafeHashMap<TKey, TValue>* map;
        private AllocatorManager.AllocatorHandle allocator;

        public SharedHashMap(int initialCapacity, AllocatorManager.AllocatorHandle allocator)
        {
            this.allocator = allocator;
            map = (UnsafeHashMap<TKey, TValue>*)UnsafeUtility.Malloc(
                UnsafeUtility.SizeOf<UnsafeHashMap<TKey, TValue>>(),
                UnsafeUtility.AlignOf<UnsafeHashMap<TKey, TValue>>(),
                allocator.ToAllocator);
            *map = new UnsafeHashMap<TKey, TValue>(initialCapacity, allocator);
        }

        public bool IsCreated => map != null && map->IsCreated;
        public int Count => map != null ? map->Count : 0;

        /// <summary>
        /// Guards the members that dereference the map. Compiled out without collections checks, so
        /// it costs nothing in a release build; the message is a constant, so Burst accepts it.
        /// </summary>
        [System.Diagnostics.Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void CheckCreated()
        {
            if (map == null)
            {
                throw new InvalidOperationException("SharedHashMap is not created. Construct it or check IsCreated first.");
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                CheckCreated();
                return (*map)[key];
            }
            set
            {
                CheckCreated();
                (*map)[key] = value;
            }
        }

        public void Add(TKey key, TValue value)
        {
            CheckCreated();
            map->Add(key, value);
        }

        public bool TryAdd(TKey key, TValue value)
        {
            CheckCreated();
            return map->TryAdd(key, value);
        }

        public bool Remove(TKey key)
        {
            CheckCreated();
            return map->Remove(key);
        }

        public void Clear()
        {
            CheckCreated();
            map->Clear();
        }

        public bool ContainsKey(TKey key)
        {
            return map != null && map->ContainsKey(key);
        }

        public bool TryGetValue(TKey key, out TValue item)
        {
            if (map == null)
            {
                item = default;
                return false;
            }

            return map->TryGetValue(key, out item);
        }

        public NativeArray<TKey> GetKeyArray(AllocatorManager.AllocatorHandle allocator)
        {
            CheckCreated();
            return map->GetKeyArray(allocator);
        }

        public NativeHashMap<TKey, TValue> ToNativeHashMap(AllocatorManager.AllocatorHandle allocator)
        {
            CheckCreated();
            var nativeMap = new NativeHashMap<TKey, TValue>(Count > 0 ? Count : 1, allocator);
            foreach (var kvp in *map)
            {
                nativeMap.Add(kvp.Key, kvp.Value);
            }

            return nativeMap;
        }

        /// <summary>
        /// The enumerator points at the heap-allocated map, so it stays valid however the caller
        /// obtained this struct (field, local, or temporary).
        /// </summary>
        public UnsafeHashMap<TKey, TValue>.Enumerator GetEnumerator()
        {
            CheckCreated();
            return map->GetEnumerator();
        }

        public void Dispose()
        {
            if (map != null)
            {
                if (map->IsCreated)
                {
                    map->Dispose();
                }

                UnsafeUtility.Free(map, allocator.ToAllocator);
                map = null;
            }

        }
    }
}

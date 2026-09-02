using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix.Net
{
    /// <summary>
    /// Maps blittable command and event types to wire ids. Ids are assigned in registration order,
    /// so both ends must register the same types in the same order: engine types first (see
    /// <c>EngineNetTypes</c> in the Simulation assembly), then game types. <see cref="Hash"/>
    /// is exchanged in the handshake to catch a mismatch.
    /// </summary>
    public sealed class NetTypeRegistry
    {
        private readonly List<Type> types = new();
        private readonly List<int> sizes = new();
        private readonly Dictionary<Type, ushort> ids = new();

        public int Count => types.Count;

        /// <summary>Registers <typeparamref name="T"/> and returns its id. Registering twice returns the same id.</summary>
        public ushort Register<T>() where T : unmanaged
        {
            Type type = typeof(T);
            if (ids.TryGetValue(type, out ushort existing))
            {
                return existing;
            }

            if (types.Count >= ushort.MaxValue)
            {
                throw new InvalidOperationException("Net type registry is full.");
            }

            ushort id = (ushort)types.Count;
            types.Add(type);
            sizes.Add(UnsafeUtility.SizeOf<T>());
            ids.Add(type, id);
            return id;
        }

        public bool TryGetId(Type type, out ushort id)
        {
            return ids.TryGetValue(type, out id);
        }

        public ushort GetId<T>() where T : unmanaged
        {
            if (!ids.TryGetValue(typeof(T), out ushort id))
            {
                throw new InvalidOperationException($"Net type {typeof(T).FullName} is not registered.");
            }

            return id;
        }

        public bool IsRegistered<T>() where T : unmanaged => ids.ContainsKey(typeof(T));

        public Type TypeOf(ushort id) => types[id];
        public int SizeOf(ushort id) => sizes[id];
        public bool IsValidId(ushort id) => id < types.Count;

        /// <summary>FNV-1a over the registered type names in id order.</summary>
        public uint Hash
        {
            get
            {
                uint hash = 2166136261u;
                for (int i = 0; i < types.Count; i++)
                {
                    string name = types[i].FullName ?? types[i].Name;
                    for (int c = 0; c < name.Length; c++)
                    {
                        hash ^= name[c];
                        hash *= 16777619u;
                    }
                    hash ^= (uint)sizes[i];
                    hash *= 16777619u;
                }

                return hash;
            }
        }
    }
}

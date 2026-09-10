using Unity.Mathematics;

namespace Caelix
{
    /// <summary>
    /// The automata half of the facade: the double-buffer phase and per-brick access, keyed by
    /// brick key. See <see cref="AutomataBrick"/> for the read/write contract.
    /// </summary>
    public unsafe partial struct VoxelEntityData
    {
        /// <summary>
        /// Opens access to one brick for an automata stage. NEVER allocates: the region that owns
        /// the brick must already exist, which it does for every brick
        /// <see cref="CollectRequiredBricks"/> returns.
        /// </summary>
        /// <remarks>
        /// When no region holds the key, the returned brick reads as empty and its writes are
        /// dropped — under collections checks a write throws instead, because that is a bug in the
        /// caller's work list, not a legitimate state.
        /// </remarks>
        public AutomataBrick OpenAutomataBrick(int3 key)
        {
            int3 sectorPos = SectorKeys.ToSectorPos(key);
            if (!sectors.TryGetValue(sectorPos, out SectorHandle handle))
            {
                handle = default;
            }

            if (!sectorNeighbors.TryGetValue(sectorPos, out SectorNeighborHandles neighbors))
            {
                neighbors = default;
            }

            return AutomataBrick.Create(key, sectorPos, handle, neighbors, OpenNeighborhood(key));
        }

        /// <summary>
        /// Opens the pending write buffer of every region that has work to do this tick, so an
        /// automata stage's writes stay invisible to its own reads. Pairs with
        /// <see cref="EndAutomataWrites"/>.
        /// </summary>
        /// <remarks>
        /// A region with no require-update flag is skipped: nothing writes into it, and the buffer
        /// copy it would need is the expensive part.
        /// </remarks>
        public void BeginAutomataWrites()
        {
            if (!sectors.IsCreated)
            {
                return;
            }

            foreach (var kvp in sectors)
            {
                if (kvp.Value.IsNull)
                {
                    continue;
                }

                if (kvp.Value.Get().sectorRequireUpdateFlags > 0)
                {
                    kvp.Value.ActivateSnapshot();
                }
            }
        }

        /// <summary>
        /// Commits the pending writes of every region and declares the storage mutated. Every
        /// pointer bound before this call is stale; see <see cref="StorageEpoch"/>.
        /// </summary>
        public void EndAutomataWrites()
        {
            if (!sectors.IsCreated)
            {
                return;
            }

            foreach (var kvp in sectors)
            {
                if (kvp.Value.IsNull)
                {
                    continue;
                }

                kvp.Value.ApplySnapshot();
            }

            // The swap exchanges whole slot tables, so every brick pointer bound before it names
            // the wrong buffer now.
            MarkStorageMutated();
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;

namespace Caelix
{
    /// <summary>
    /// The application's rules for which simulation bits (<see cref="BrickUpdateFlags.AutomataMask"/>)
    /// a voxel write raises when the writer supplies no mask of its own.
    /// </summary>
    /// <remarks>
    /// Two tables, both read only by the DEFAULT setter bodies (<c>SetBlock(pos, block)</c>,
    /// <c>SetSlot(slot, pos, value)</c>); the explicit overloads that take a mask never touch them:
    /// <list type="bullet">
    ///   <item>Block writes raise <c>Blocks[previous.id] | Blocks[value.id]</c>, so both introducing
    ///   and removing a block that some automaton cares about schedules that automaton.</item>
    ///   <item>Writes to any other slot raise <c>Slots[slotId]</c>. Raw slot values are never
    ///   classified: what a Meta value means depends on the block it sits under.</item>
    /// </list>
    /// The tables live in Burst shared statics so the same rules apply from managed code and from
    /// every job, in every world. Configure them once, before the first generic write and before the
    /// first tick (a <c>RuntimeInitializeOnLoadMethod</c> is a good place), and leave them alone
    /// while the simulation runs: nothing synchronizes readers against a rewrite. An Editor domain
    /// reload resets them to <see cref="BrickUpdateFlags.None"/>.
    /// </remarks>
    public static unsafe class AutomataDefaults
    {
        /// <summary>One entry per 16-bit block id.</summary>
        internal struct BlockTable
        {
            public fixed ushort Flags[BlockTableSize];
        }

        /// <summary>One entry per sector slot, plus the legacy load mapping.</summary>
        internal struct SlotTable
        {
            public fixed ushort Flags[Sector.MAX_SLOTS];
            public ushort LegacyGeneralAutomata;
        }

        public const int BlockTableSize = 1 << 16;

        // SharedStatic keys must be non-static types.
        private class BlocksKey { }
        private class SlotsKey { }

        private static readonly SharedStatic<BlockTable> s_blocks = SharedStatic<BlockTable>.GetOrCreate<BlocksKey>();
        private static readonly SharedStatic<SlotTable> s_slots = SharedStatic<SlotTable>.GetOrCreate<SlotsKey>();

        // ---- Configuration (managed, before the simulation starts) ------------------------------

        /// <summary>Sets the simulation bits a default Block write raises for <paramref name="blockId"/> (as previous or as replacement).</summary>
        public static void SetBlockDefault(ushort blockId, BrickUpdateFlags flags)
        {
            s_blocks.Data.Flags[blockId] = (ushort)(flags & BrickUpdateFlags.AutomataMask);
        }

        /// <summary>Sets the simulation bits a default write to a non-Block slot raises. Ignored for the Block slot, which uses the block table.</summary>
        public static void SetSlotDefault(SectorSlotId slot, BrickUpdateFlags flags)
        {
            int index = (int)slot;
            if (index < 0 || index >= Sector.MAX_SLOTS) throw new ArgumentOutOfRangeException(nameof(slot));
            s_slots.Data.Flags[index] = (ushort)(flags & BrickUpdateFlags.AutomataMask);
        }

        /// <summary>
        /// What the loader turns a pre-v8 save's pending bit 0 (the old <c>GeneralAutomata</c>) into.
        /// Applied only to bricks that already had that work pending; nothing idle wakes.
        /// </summary>
        public static BrickUpdateFlags LegacyGeneralAutomataFlags
        {
            get => (BrickUpdateFlags)s_slots.Data.LegacyGeneralAutomata;
            set => s_slots.Data.LegacyGeneralAutomata = (ushort)(value & BrickUpdateFlags.AutomataMask);
        }

        /// <summary>Clears every rule. For tests; call only while no job can read the tables.</summary>
        public static void Reset()
        {
            fixed (ushort* blocks = s_blocks.Data.Flags)
            {
                UnsafeUtility.MemClear(blocks, BlockTableSize * sizeof(ushort));
            }

            fixed (ushort* slots = s_slots.Data.Flags)
            {
                UnsafeUtility.MemClear(slots, Sector.MAX_SLOTS * sizeof(ushort));
            }

            s_slots.Data.LegacyGeneralAutomata = 0;
        }

        // ---- Lookups (the default setter bodies only) -------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort BlockWriteFlags(ushort previousId, ushort nextId)
        {
            ref BlockTable table = ref s_blocks.Data;
            return (ushort)(table.Flags[previousId] | table.Flags[nextId]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort SlotWriteFlags(int slotId)
        {
            return s_slots.Data.Flags[slotId];
        }
    }
}

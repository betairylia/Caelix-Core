# Caelix Core documentation

Core provides voxel storage, neighborhood reads, dirty propagation, persistence,
tick-hook primitives, and message/channel primitives. Start with the storage
model before changing traversal or allocation.

| Task | Read |
|---|---|
| Understand the data layout | [The VDB-like storage model](internals/voxel-storage.md) |
| Traverse voxel data correctly | [Enumerator rules and inventory](reference/enumerators.md) |
| Create a world and connect a client | [Caelix getting started](https://github.com/betairylia/Caelix/blob/main/Documentation~/manual/get-started.md) |
| Write an automaton | [Automata guide](https://github.com/betairylia/Caelix/blob/main/Documentation~/manual/automata.md) |
| Understand tick phases | [Tick and dirty propagation](https://github.com/betairylia/Caelix/blob/main/Documentation~/internals/tick-and-dirty.md) |
| Write or update documentation | [Shared documentation guide](https://github.com/betairylia/Caelix/blob/main/Documentation~/documentation-guide.md) |

Pages record their reviewed revisions. Cross-repository links target `main` and
may await merging; local source links follow this checkout.

## Implementation and tests

- [Runtime sources](../Runtime)
- [Editor tests](../Tests/Editor)
- [Package dependencies](../package.json)
- [Contributor rules](../AGENTS.md)

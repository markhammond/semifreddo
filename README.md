# Semifreddo — delectable semi-frozen collections for .NET

[![NuGet](https://img.shields.io/nuget/v/Semifreddo.svg)](https://www.nuget.org/packages/Semifreddo) [![CI](https://github.com/markhammond/semifreddo/actions/workflows/ci.yml/badge.svg)](https://github.com/markhammond/semifreddo/actions/workflows/ci.yml)

An opinionated high-performance persistent collections library for modern .NET, with structural sharing and allocation behavior as first-class design objectives. Microsoft's *frozen* collections are built once and then only read; **Semifreddo** collections are *partially frozen* (Italian *semifreddo*, "half-cold"): never changed in place, yet every change returns a new version, sharing almost all of its structure with the old one, and the old version stays valid and unchanged. Map, set and vector edits cost O(log n); a DAG edge is checked against the part of the graph it can reach.

Semifreddo does not compete on breadth. It offers a small set of collections, each built to be fast and to allocate predictably: transients for batches of edits, which change a version's own new nodes in place instead of copying a path per edit, and zero-allocation enumeration when using ref-struct enumerators.

- `ImmVector<T>` (persistent vector) and `ImmFlexVector<T>` (RRB vector: fast concat, split, insert)
- `ImmMap<K, V>`, `ImmSet<T>` (HAMT) and `ImmOrderedMap<K, V>` (insertion-ordered)
- `ImmDag<N, A, V>`: a persistent directed acyclic graph with alternate keys and a change log, kept in step with a topological order if you pass one (`dag.AddEdge(ref order, a, b)`: the order proves each edge acyclic, O(1) when it agrees); `DagBatchOptions` can skip the cycle check or the change log
- `ImmPkTopoOrder`, `ImmLeveledPkTopoOrder`: incremental topological orders (Pearce–Kelly), plain or leveled flavor
- `RetiringDag<N, A, V>` (`Semifreddo.Reclamation`): deferred reclamation for a DAG's payloads, released only once no reader of an older version can still see them
- Transients for batch edits, ref-struct enumerators (no allocation), `Semifreddo.IImmutable` as a share-don't-copy marker
- AOT and trimming compatible
- Equality is by version, not contents: two collections are equal only if they are the same version (`IsSameAs`)

```csharp
using Semifreddo;

var a = ImmMap<string, int>.Empty.SetItem("pistachio", 3);
var b = a.SetItem("hazelnut", 5);   // a is unchanged: two versions, shared structure
var v = ImmVector<int>.Empty.Add(1).Add(2);
```

A graph that stays acyclic, with a topological order that proves each new edge as it goes:

```csharp
using Semifreddo;

// A recipe's steps (int ids, names as alternate keys) and a topological order kept in step with them.
var recipe = ImmDag.Create<int, string>();
var order = ImmPkTopoOrder.CreateWithMinValueSentinel<int>();

recipe = recipe.Mutate(ref order, static s =>
{
    s.AddNode(1, "whip the cream");
    s.AddNode(2, "whisk the eggs and sugar");
    s.AddNode(3, "fold together");
    s.AddEdge(1, 3);                             // folding needs the whipped cream ...
    s.AddEdge(2, 3);                             // ... and the whisked eggs
});

// The order checks each new edge, instead of a search of the graph: O(1) when the edge agrees with it.
recipe = recipe.AddNode(ref order, 4, "freeze", default).AddEdge(ref order, 3, 4);

// An edge that would close a cycle is refused, and neither the graph nor the order changes.
try { recipe = recipe.AddEdge(ref order, 4, 1); }   // freezing before whipping the cream?
catch (DagCycleException<int> e) { Console.WriteLine(string.Join(" -> ", e.Cycle)); }   // 1 -> 3 -> 4 -> 1

Console.WriteLine(string.Join(", ", order));   // 1, 2, 3, 4: whip and whisk, then fold, then freeze
```

## Usage

Install [Semifreddo from NuGet](https://www.nuget.org/packages/Semifreddo):

```bash
dotnet add package Semifreddo
```

## Performance

Against System.Collections.Immutable (`ImmutableList<T>`, `ImmutableDictionary<K, V>`) and other persistent collections for .NET, at N = 100,000 (`long` elements, keys and values). Each cell is run time / memory allocated, relative to System.Collections.Immutable: lower is better. Reads allocate nothing there, so for them the allocation is in bytes. † marks a library with no batch facility, which makes the edits one by one. LanguageExt's `Seq` has no update by index. Measured with BenchmarkDotNet (one launch, 3 warm-up and 10 measured iterations) on .NET 10.0.13; the benchmarks, library versions and what each one does are in [bench/Semifreddo.Comparison](https://github.com/markhammond/semifreddo/tree/main/bench/Semifreddo.Comparison).

Vectors:

| | `ImmVector` | `ImmFlexVector` | LanguageExt `Seq` | LanguageExt `Lst` | FSharpx `PersistentVector` | Durable7 `RrbVector` |
|---|---|---|---|---|---|---|
| Append | **0.035** / 0.088 | 0.044 / 0.11 | 0.051 / **0.084** | 0.98 / 1.1 | 0.34 / 0.36 | 1.5 / 2.6 |
| Branching append | **0.084** / **0.26** | 0.097 / 0.28 | 76 / 358 | 0.91 / 1.1 | 0.35 / 0.36 | 1.9 / 2.6 |
| RemoveLast | 0.050 / 0.11 | 0.074 / 0.14 | **0.036** / **0.082** | 0.76 / 1.0 | 1.0 / 1.3 | 3.2 / 4.2 |
| Update | **0.20** / 1.5 | **0.20** / 1.6 | — | 0.89 / 1.0 | 0.69 / 1.7 | 0.37 / **0.86** |
| Index | 0.022 / **0** | 0.030 / **0** | **0.0095** / **0** | 1.1 / **0** | 0.12 / **0** | 0.041 / **0** |
| Iterate | **0.071** / **0** | 0.078 / **0** | 0.19 / 48 B | 0.65 / 32 B | 1.0 / 232 B | 0.46 / 2.3 KB |
| Build | 0.064 / 0.19 | 0.082 / 0.19 | **0.039** / **0.17** | 5.9 / 1.0 | 1.5 / 0.71 | 0.092 / 0.23 |
| Batch append | **0.066** / **0.19** | 0.081 / **0.19** | 0.11 / 0.60 | 5.9 / 1.0 | 6.9 / 6.2 † | 0.088 / 0.23 |
| Batch update | **0.026** / **0.23** | 0.042 / **0.23** | — | 3.3 / 20 † | 2.5 / 33 † | 1.3 / 17 † |

Hash maps:

| | `ImmMap` | LanguageExt `HashMap` | FSharpx `PersistentHashMap` | Durable7 `PersistentHashMap` |
|---|---|---|---|---|
| Add | **0.41** / **0.93** | 0.46 / 1.0 | 1.0 / 1.2 | 0.55 / 1.0 |
| Update | 0.56 / 1.0 | **0.52** / 1.0 | 1.3 / 1.4 | 0.72 / 1.1 |
| Remove | **0.46** / 1.0 | **0.46** / 1.0 | 1.2 / 1.4 | 0.59 / 1.1 |
| Lookup (hit) | **0.11** / **0** | 0.21 / 576 B | 0.94 / 12 MB | 0.22 / **0** |
| Lookup (miss) | **0.063** / **0** | 0.11 / **0** | 0.45 / 9.6 MB | 0.088 / **0** |
| Iterate | **0.16** / **0** | 1.0 / 2.3 MB | 4.4 / 13 MB | 0.37 / **0** |
| Build | **0.36** / 1.4 | 1.4 / 18 | 2.6 / 4.7 | 1.3 / 13 |
| Batch insert | **0.59** / 1.3 | 1.1 / 9.5 | 2.3 / 12 † | 0.89 / 1.8 |
| Batch update | **0.16** / **0.55** | 1.5 / 13 | 3.4 / 17 † | 0.59 / 0.64 |
| Batch remove | **0.32** / **0.89** | 1.1 / 19 | 2.9 / 26 † | 0.94 / 4.9 |

DAGs, indicative: no other library has a persistent DAG, so the comparison builds one from each library's own collections, as their users would write it (a map of nodes with successor and predecessor sets, a depth-first search to refuse a cycle, Kahn's algorithm for a topological order). The baseline is that DAG over System.Collections.Immutable. `ImmPkTopoOrder` is an incremental topological order kept in step with the graph; walking the edges is the same with or without it.

| | `ImmDag` | `ImmDag` + `ImmPkTopoOrder` | LanguageExt DAG |
|---|---|--|---|
| Build | 0.49 / 0.99 | 0.82 / 1.6 | **0.47** / **0.85** |
| Batch build | **0.24** / **0.60** | 0.46 / 2.8 | 0.86 / 7.1 † |
| Add edge | 0.40 / **0.80** | **0.36** / 1.2 | 0.45 / 0.86 |
| Refuse cycle | 0.39 / 0.37 | **0.13** / **0.35** | 0.47 / 4.4 |
| Remove node | **0.49** / 0.91 | 0.76 / 1.4 | 0.58 / **0.89** |
| Walk edges | **0.17** / **0** | **0.17** / **0** | 0.44 / 5.9 MB |
| Topological order | 0.33 / 1.0 | **0.011** / **0.21** | 0.67 / 3.3 |

## Shared tails

`ImmVector` and `ImmFlexVector` keep their last elements (up to 32) in a tail that versions share. The first append to a version claims the tail's next slot atomically and writes the element there, so the new version costs only a small header; a second append to the same version copies the tail instead. Each slot is written once, so no version ever sees another's elements, whichever threads append.

The cost is retention: a version's tail can hold up to 31 elements appended by versions made from it, and keeps them reachable. Pops and `Take` share the tail only when the elements hold no references, and copy it otherwise, so removed references are released. Call `TrimExcess()` on a version kept long after its relatives to let their elements go; it returns the same version when there is nothing to release.

```csharp
var draft = ImmVector<string>.Empty.Add("whip the cream");
var undo = draft;                            // kept for undo
draft = draft.Add("fold in the meringue");   // written into the tail undo shares
undo = undo.TrimExcess();                    // undo's tail no longer holds the new step
```

## Deferred reclamation

A persistent graph lets readers keep old versions, so a payload removed from the latest version may still be in use. `RetiringDag<N, A, V>`, in `Semifreddo.Reclamation`, pairs an `ImmDag<N, A, V>` with a retirement ledger: a payload that leaves the graph retires, and is reclaimed only once no reader can still see it (the retire / reclaim lifecycle of safe memory reclamation).

- A payload retires when its node is removed or given a new payload, with the id and alternate key it had; one that only moved (a rename) does not.
- A payload that comes back (an undo, under any id) is live again, and its retirement is dropped.
- Reclaiming matches each candidate by a stamp that is never reused, so a payload that came back meanwhile is neither reclaimed nor released.
- A single-reference readonly struct: tear-free, and updated with compare-and-swap (`RetiringDag.Update`, `RetiringDag.Reclaim`).

```csharp
using System.Buffers;
using Semifreddo.Reclamation;

var dag = RetiringDag<int, string, byte[]>.Empty
    .AddNode(1, "input", ArrayPool<byte>.Shared.Rent(4096))
    .AddNode(2, "output", ArrayPool<byte>.Shared.Rent(4096));
dag = dag.RemoveNode(1);                     // node 1's buffer retires: a reader of an older version may hold it

// The reclaimer: read the candidates, wait until every reader of an older version is done, then reclaim and release.
var candidates = dag.Retired.ToArray();
var reclaimed = new List<RetiredNode<int, string, byte[]>>();
RetiringDag.Reclaim(ref dag, candidates, reclaimed);
foreach (var r in reclaimed) ArrayPool<byte>.Shared.Return(r.Value);
```

## Credits

The vector, RRB flex vector, CHAMP map/set and transient designs follow the C++ [immer](https://github.com/arximboldi/immer) library by Juan Pedro Bolívar Puente, and Clojure's persistent vector; the incremental topological order is Pearce and Kelly's (2006).

## Build

```bash
dotnet test Semifreddo.slnx
```

```bash
dotnet run -c Release --project bench/Semifreddo.Bench
```

## License

MIT. See [LICENSE](https://github.com/markhammond/semifreddo/blob/main/LICENSE).

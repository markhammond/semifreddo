# Semifreddo.Comparison

Semifreddo against other persistent (immutable, structurally shared) collections for .NET: run time and memory
allocated per operation, measured with [BenchmarkDotNet](https://benchmarkdotnet.org).

| Library | Version | Vector | Hash map | DAG |
|---|---|---|---|---|
| Semifreddo | this repository | `ImmVector<T>`, `ImmFlexVector<T>` (RRB) | `ImmMap<K, V>` (CHAMP) | `ImmDag<N, A, V>`, with or without an `ImmPkTopoOrder` |
| System.Collections.Immutable (the baseline) | in .NET 10 | `ImmutableList<T>` (AVL tree) | `ImmutableDictionary<K, V>` (AVL tree of hash buckets) | `ImmutableDictionaryDag` (here) |
| [LanguageExt.Core](https://github.com/louthy/language-ext) | 5.0.0-beta-78 | `Seq<A>`, `Lst<A>` (AVL tree) | `HashMap<K, V>` (CHAMP) | `HashMapDag` (here) |
| [FSharpx.Collections](https://github.com/fsprojects/FSharpx.Collections) | 3.1.0 | `PersistentVector<T>` | `PersistentHashMap<K, V>` (Clojure's HAMT) | |
| [Durable7](https://github.com/VladimirReshetnikov/Durable7) | commit `6ca5902` | `RrbVector<T>` | `PersistentHashMap<K, V>` (CHAMP) | |

Each benchmark runs one operation N times (N = 1,000 and 100,000), every intermediate version a value of its own, so
the numbers include what persistence costs. Within each category the baseline is System.Collections.Immutable: the
Ratio and Alloc Ratio columns read relative to it.

- **Vectors** (`long`): Append (N appends to an empty vector), Build (from an array, each library's bulk path), Index
  (N random reads), Update (N random replacements), Iterate (`foreach`), RemoveLast (drain from the end), and
  Branching append (N steps, each appending twice to the same version and going on from the second: Append is a
  structure's best case when it appends in place into spare capacity, as LanguageExt's `Seq` does, which holds for
  only one of two branches). LanguageExt's `Seq` has no update by index, so it sits out Update.
- **Hash maps** (`long` to `long`, random keys): Add (N inserts into an empty map), Build (from N pairs), Lookup (hit)
  and Lookup (miss), Update (N replacements of present keys), Remove (every key), Iterate. A hit reads the value
  through each map's own single-lookup path; a miss asks `ContainsKey`.
- **DAGs** (`long` ids, alternate keys and payloads): no other library has a persistent DAG, so `Dags/` holds the one
  you would assemble from each library's collections, written as their users would write it: a map of nodes (with
  successor and predecessor sets) and a map of alternate keys, a new edge checked by a depth-first search from its
  target for its source, Kahn's algorithm for a topological order. The two are the same code over
  System.Collections.Immutable (`ImmutableDictionary`, `ImmutableHashSet`, the baseline) and LanguageExt (`HashMap`,
  `HashSet`), so they differ only in the collections. `ImmDag` runs as is (a search from the new edges' targets) and
  `_WithOrder` keeps an `ImmPkTopoOrder` in step, which proves each edge without a search. The graph is groups of 32
  nodes, each a random DAG with about two edges into each node, so a search stays within a group. Categories: Build
  (nodes, then shuffled edges, one by one), Batch build, Add edge (to the built graph), Refuse cycle (each edge
  reversed), Remove node, Walk edges and Topological order.
- **Batches**: the same edits made to an existing collection as one batch, giving one new version, through each
  library's batch facility: a transient (Semifreddo's `Mutate`, Durable7's map transient), a builder
  (System.Collections.Immutable, Durable7's vector appends) or a range method. A library with none makes the edits one
  by one (the `_PerEdit` benchmarks). Vectors: Batch update (the Update edits) and Batch append (N more items onto N).
  Maps: Batch update, Batch insert (N absent keys) and Batch remove.

## Running

Durable7 is not on NuGet; fetch it first (a clone at the pinned commit, into the git-ignored `external/`). Without it,
its benchmarks are left out and the rest run as usual.

```bash
./fetch-durable7.sh
```

Then, from this folder (`--job short` is quicker and noisier; leave it out for BenchmarkDotNet's default):

```bash
dotnet run -c Release -- --filter '*' --job short
```

`--filter '*Map*'`, `--filter '*Vector*'` or `--filter '*Dag*'` runs one suite; `--list flat` lists every benchmark.
`SEMIFREDDO_BENCH_N` limits the sizes (comma-separated). The tables in the top-level README come from N = 100,000
with fixed iterations, about 35 minutes for all three suites:

```bash
SEMIFREDDO_BENCH_N=100000 dotnet run -c Release -- --filter '*' --launchCount 1 --warmupCount 3 --iterationCount 10
```

Reports land in `BenchmarkDotNet.Artifacts/results/`. Run on a quiet machine: other load inflates both times and their
spread.

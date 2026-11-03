using System.Diagnostics;
using Semifreddo.Conformance;
using ZLinq;

namespace Semifreddo.Bench;

/// <summary>
/// Enumeration hot paths per collection, in ns per element: duck-typed <c>foreach</c> (ref struct enumerator), a generic
/// host over <see cref="IRefEnumerable{T, TEnumerator}"/> (constrained calls; what any adapter does), plain LINQ (boxes
/// the collection and its cursor) and ZLinq through the test-side bridge. Values keep int sums from overflowing.
/// </summary>
internal static class EnumBench
{
    private static long s_sink = 0;
    private const int Big = 1_000_000, Mid = 60_000;

    public static void Run()
    {
        var arr = Enumerable.Range(0, Big).Select(i => i & 1023).ToArray();
        var vec = ImmVector.Create(arr);
        var flex = ImmFlexVector<int>.Empty;
        var rnd = new Random(1);
        for (var i = 0; i < Big;) { var k = Math.Min(Big - i, 1 + rnd.Next(64)); flex += ImmFlexVector.Create(arr.AsSpan(i, k)); i += k; }
        var set = ImmSet.Create(Enumerable.Range(0, Mid).ToArray());
        var bigSet = ImmSet.Create(Enumerable.Range(0, Big).ToArray());
        var map = ImmMap.CreateRange(Enumerable.Range(0, Mid).Select(i => KeyValuePair.Create(i, i & 1023)));
        var omap = ImmOrderedMap.CreateRange(Enumerable.Range(0, Mid + Mid / 7).Select(i => KeyValuePair.Create(i, i & 1023)));
        for (var i = 0; i < Mid + Mid / 7; i += 7) omap = omap.Remove(i);   // tombstones
        var order = ImmPkTopoOrder<int, MinValueSentinel<int>>.Empty;
        for (var i = 0; i < Mid + Mid / 7; i++) order = order.Add(i);
        for (var i = 0; i < Mid + Mid / 7; i += 7) order = order.Remove(i);   // removal holes, as in real use

        Header($"int[] {Big:N0} (baseline)");
        Measure("foreach", Big, () => { long s = 0; foreach (var x in arr) s += x; s_sink += s; });

        Header($"ImmVector<int> {Big:N0}");
        Measure("foreach (duck-typed ref struct)", Big, () => s_sink += Foreach(vec));
        Measure("Chunks (span per leaf)", Big, () => { long s = 0; foreach (var c in vec.Chunks) foreach (var x in c) s += x; s_sink += s; });
        Measure("generic host (IRefEnumerable)", Big, () => s_sink += Host<ImmVector<int>, ImmVector<int>.Enumerator>(vec));
        Measure("LINQ Sum", Big, () => s_sink += vec.Sum());
        Measure("ZLinq Sum", Big, () => s_sink += vec.AsValueEnumerable().Sum());

        Header($"ImmFlexVector<int> {Big:N0} (relaxed)");
        Measure("foreach (duck-typed ref struct)", Big, () => s_sink += Foreach(flex));
        Measure("Chunks (span per leaf)", Big, () => { long s = 0; foreach (var c in flex.Chunks) foreach (var x in c) s += x; s_sink += s; });
        Measure("generic host (IRefEnumerable)", Big, () => s_sink += Host<ImmFlexVector<int>, ImmFlexVector<int>.Enumerator>(flex));
        Measure("LINQ Sum", Big, () => s_sink += flex.Sum());
        Measure("ZLinq Sum", Big, () => s_sink += flex.AsValueEnumerable().Sum());

        Header($"ImmSet<int> {Mid:N0}");
        Measure("foreach (duck-typed ref struct)", Mid, () => s_sink += Foreach(set));
        Measure("generic host (IRefEnumerable)", Mid, () => s_sink += Host<ImmSet<int>, ImmSet<int>.Enumerator>(set));
        Measure("LINQ Sum", Mid, () => s_sink += set.Sum());
        Measure("ZLinq Sum", Mid, () => s_sink += set.AsValueEnumerable().Sum());

        Header($"ImmSet<int> {Big:N0} (memory-bound; long sums only)");
        Measure("foreach (duck-typed ref struct)", Big, () => s_sink += Foreach(bigSet));
        Measure("generic host (IRefEnumerable)", Big, () => s_sink += Host<ImmSet<int>, ImmSet<int>.Enumerator>(bigSet));

        Header($"ImmMap<int, int> {Mid:N0} (sum of values; LINQ/ZLinq pay a selector delegate)");
        Measure("foreach (duck-typed ref struct)", Mid, () => s_sink += Foreach(map));
        Measure("foreach + CurrentValue (by ref)", Mid, () => s_sink += ForeachByRef(map));
        Measure("generic host (IRefEnumerable)", Mid, () => s_sink += HostValues<ImmMap<int, int>, ImmMap<int, int>.Enumerator>(map));
        Measure("LINQ Sum(selector)", Mid, () => s_sink += map.Sum(static kv => kv.Value));
        Measure("ZLinq Sum(selector)", Mid, () => s_sink += map.AsValueEnumerable().Sum(static kv => kv.Value));

        Header($"ImmOrderedMap<int, int> {Mid:N0} (sum of values, with tombstones)");
        Measure("foreach (duck-typed ref struct)", Mid, () => s_sink += Foreach(omap));
        Measure("generic host (IRefEnumerable)", Mid, () => s_sink += HostValues<ImmOrderedMap<int, int>, ImmOrderedMap<int, int>.Enumerator>(omap));
        Measure("LINQ Sum(selector)", Mid, () => s_sink += omap.Sum(static kv => kv.Value));
        Measure("ZLinq Sum(selector)", Mid, () => s_sink += omap.AsValueEnumerable().Sum(static kv => kv.Value));

        Header($"ImmPkTopoOrder {Mid:N0} nodes (with removal holes)");
        Measure("foreach (duck-typed ref struct)", Mid, () => s_sink += Foreach(order));
        Measure("generic host (IRefEnumerable)", Mid, () => s_sink += Host<ImmPkTopoOrder<int, MinValueSentinel<int>>, TopoEnumerator<int, MinValueSentinel<int>>>(order));
        Measure("LINQ Sum", Mid, () => s_sink += order.Sum());
        Measure("ZLinq Sum", Mid, () => s_sink += order.AsValueEnumerable().Sum());

        // Edge sets: inline (<= 16, an array) vs CHAMP, via EdgeSet's own enumerator and via Adjacency<N> (what the
        // topological-order searches and DAG validation iterate). The graph walk enumerates every node's successors:
        // dominated by per-enumerator setup on small sets.
        const int g = 100_000;
        var graph = ImmDag<int, int, NoPayload>.Empty.Mutate(g, static (s, n) =>
        {
            for (var i = 0; i < n; i++) s.AddNode(i, i);
            for (var i = 0; i < n; i++) for (var k = 1; k <= 3; k++) if (i + k * 7 < n) s.AddEdge(i, i + k * 7);
            for (var k = 1; k <= 2000; k++) s.AddEdge(0, k);   // one wide fan-out node (CHAMP edge set)
            for (var k = 1; k <= 8; k++) s.AddEdge(1, 100 + k); // an inline edge set of 11
        }, new DagBatchOptions { SkipCycleCheck = true });
        var wide = graph.Single(v => v.Id == 0).Successors;
        var narrow = graph.Single(v => v.Id == 1).Successors;
        long edges = graph.EdgeCount;
        Header($"EdgeSet: {narrow.Count} inline / {wide.Count} CHAMP successors; graph walk {g:N0} nodes, {edges:N0} edges");
        Measure("EdgeSet inline foreach (per enumeration)", 1, () => s_sink += SumEdges(narrow));
        Measure("EdgeSet CHAMP foreach", wide.Count, () => s_sink += SumEdges(wide));
        Measure("Adjacency CHAMP foreach (IDagAdjacency)", wide.Count, () => s_sink += SumAdjacency(graph, 0));
        Measure("graph walk: all successors via Adjacency", (int)edges, () => s_sink += WalkAll(graph, g));

        Header($"ImmVector<int> {Big:N0}: positional and bulk (per call)");
        Measure("LINQ Last (no IList<T>: full scan)", 1, () => s_sink += vec.Last());
        Measure("ZLinq Last (TryCopyTo: O(log n))", 1, () => s_sink += vec.AsValueEnumerable().Last());
        Measure("LINQ ElementAt(n/2)", 1, () => s_sink += vec.ElementAt(Big / 2));
        Measure("ZLinq ElementAt(n/2)", 1, () => s_sink += vec.AsValueEnumerable().ElementAt(Big / 2));
        Measure("LINQ ToArray", 1, () => s_sink += Enumerable.ToArray(vec).Length);
        Measure("ZLinq ToArray (chunked TryCopyTo)", 1, () => s_sink += vec.AsValueEnumerable().ToArray().Length);
        Measure("instance ToArray", 1, () => s_sink += vec.ToArray().Length);
    }

    private static long Foreach(ImmVector<int> v) { long s = 0; foreach (var x in v) s += x; return s; }
    private static long Foreach(ImmFlexVector<int> v) { long s = 0; foreach (var x in v) s += x; return s; }
    private static long Foreach(ImmSet<int> v) { long s = 0; foreach (var x in v) s += x; return s; }
    private static long Foreach(ImmMap<int, int> m) { long s = 0; foreach (var kv in m) s += kv.Value; return s; }
    private static long Foreach(ImmOrderedMap<int, int> m) { long s = 0; foreach (var kv in m) s += kv.Value; return s; }
    private static long Foreach(ImmPkTopoOrder<int, MinValueSentinel<int>> o) { long s = 0; foreach (var x in o) s += x; return s; }

    private static long SumEdges(EdgeSet<int> e) { long s = 0; foreach (var x in e) s += x; return s; }

    private static long SumAdjacency<TGraph>(TGraph g, int id) where TGraph : IDagAdjacency<int>
    {
        long s = 0;
        foreach (var x in g.Successors(id)) s += x;
        return s;
    }

    private static long WalkAll<TGraph>(TGraph g, int n) where TGraph : IDagAdjacency<int>
    {
        var s = 0L;
        for (var i = 0; i < n; i++) foreach (var x in g.Successors(i)) s += x;
        return s;
    }

    private static long ForeachByRef(ImmMap<int, int> m)
    {
        var s = 0L;
        using var e = m.GetEnumerator();
        while (e.MoveNext()) s += e.CurrentValue;
        return s;
    }

    private static long Host<TSource, TEnumerator>(TSource source)
        where TSource : struct, IRefEnumerable<int, TEnumerator>
        where TEnumerator : struct, IEnumerator<int>, allows ref struct
    {
        long s = 0;
        using var e = source.GetEnumerator();     // the interface's GetEnumerator: constrained calls throughout
        while (e.MoveNext()) s += e.Current;
        return s;
    }

    private static long HostValues<TSource, TEnumerator>(TSource source)
        where TSource : struct, IRefEnumerable<KeyValuePair<int, int>, TEnumerator>
        where TEnumerator : struct, IEnumerator<KeyValuePair<int, int>>, allows ref struct
    {
        long s = 0;
        using var e = source.GetEnumerator();
        while (e.MoveNext()) s += e.Current.Value;
        return s;
    }

    private static void Header(string title) => Console.WriteLine($"--- {title}");

    /// <summary>ns per element (or per call when elements = 1), bytes per call. Time-based warm-up: tier-1 promotion
    /// waits for a quiet period, so a short fixed warm-up can leave a case on tier-0 code.</summary>
    private static void Measure(string name, int elements, Action body)
    {
        var warm = Stopwatch.StartNew();
        var calls = 0;
        while (warm.ElapsedMilliseconds < 1000) { body(); calls++; }
        var reps = Math.Max(1, (int)(calls * 50.0 / warm.ElapsedMilliseconds));   // ~50 ms per trial
        var best = double.MaxValue; 
        var bytes = 0L;
        for (var r = 0; r < 5; r++)
        {
            GC.Collect();
            var a = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (var k = 0; k < reps; k++) body();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / reps);
            bytes = (GC.GetAllocatedBytesForCurrentThread() - a) / reps;
        }
        var unit = elements == 1 ? "ns/call" : "ns/elem";
        Console.WriteLine($"  {name,-44} {best / elements,10:F2} {unit}  {bytes,9:N0} B/call");
    }
}
using System.Diagnostics;
using Semifreddo;
using Semifreddo.Testing;
using IntTopoOrder = Semifreddo.ImmLeveledPkTopoOrder<int, Semifreddo.MinValueSentinel<int>>;
using IntOrderOnly = Semifreddo.ImmPkTopoOrder<int, Semifreddo.MinValueSentinel<int>>;

/// <summary>ImmPkTopoOrder (incremental) vs KahnTopoOrder (full recompute oracle).
/// Run: dotnet run -c Release --project bench/Semifreddo.Bench -- topo</summary>
internal static class TopoBench
{
    public static void Run()
    {
        const int V = 10_000, E = 30_000;
        var rnd = new Random(11);
        var edges = new List<(int, int)>();
        var seen = new HashSet<(int, int)>();
        while (edges.Count < E)
        {
            int a = rnd.Next(V - 1), b = a + 1 + rnd.Next(Math.Min(400, V - a - 1)); // id-forward => acyclic
            if (seen.Add((a, b))) edges.Add((a, b));
        }
        var dags = new ImmDag<int, int, NoPayload>[E]; // dags[k] holds edges[0..k]
        var d = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < V; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
        for (var k = 0; k < E; k++) dags[k] = d = d.Mutate(edges[k], static (s, e) => s.AddEdge(e.Item1, e.Item2), new DagBatchOptions { SkipCycleCheck = true });
        var full = dags[^1];
        var edgeArr = edges.ToArray();

        Console.WriteLine($"--- {V:N0} nodes inserted in reverse id order, {E:N0} id-forward edges (most disagree with the initial order)");
        Section<IntTopoOrder>("ImmLeveledPkTopoOrder", dags, edgeArr, full, E);
        Section<IntOrderOnly>("ImmPkTopoOrder", dags, edgeArr, full, E);
        Section<KahnTopoOrder<int>>("Kahn oracle", dags, edgeArr, full, 300); // O(V + E) per edge: sample a prefix

        Console.WriteLine("--- failing edge on a 1,000-node chain: probe vs cycle-reporting");
        {
            const int n = 1000;
            var chain = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < n; i++) s.AddNode(i, i); for (var i = 0; i + 1 < n; i++) s.AddEdge(i, i + 1); }, new DagBatchOptions { SkipCycleCheck = true });
            var cyclic = chain.Mutate(static s => s.AddEdge(n - 1, 0), new DagBatchOptions { SkipCycleCheck = true });
            var order = IntTopoOrder.Empty.Mutate(0, static (t, _) => { for (var i = 0; i < n; i++) t.Add(i); });
            Measure("TryAddEdge(..., out cycle) failing", 1000, () => { for (var k = 0; k < 1000; k++) order.TryAddEdge(cyclic, n - 1, 0, out _, out _); });
            Measure("TryAddEdge(..., out result) probe failing", 1000, () => { for (var k = 0; k < 1000; k++) order.TryAddEdge(cyclic, n - 1, 0, out _); });
        }

        Console.WriteLine("--- levels on the final 10k-node / 30k-edge order");
        {
            var o = IntTopoOrder.Empty.Mutate(0, static (t, _) => { for (var i = 10_000 - 1; i >= 0; i--) t.Add(i); });
            if (!o.TryAddEdges(full, edgeArr, out o)) throw new Exception();
            Console.WriteLine($"  levels={o.LevelCount}, widest={Enumerable.Range(0, o.LevelCount).Max(l => o.LevelWidth(l))}");
            Measure("Tighten (exact levels, O(V+E))", o.Count, () => o.Tighten(full));
            var nodes = new int[o.Count];
            var starts = new int[o.LevelCount + 1];
            Measure("CopyByLevel (groups into caller buffers)", o.Count, () => o.CopyByLevel(nodes, starts));
            var bottom = new Dictionary<int, double>();
            Func<int, double> cost = static n => 1 + (n % 7);
            Measure("ComputeBottomLevels (critical path)", o.Count, () => o.ComputeBottomLevels(full, cost, bottom));
        }

        Console.WriteLine("--- retained memory of a 1,000,000-node order (int ids)");
        Footprint<IntTopoOrder>("ImmLeveledPkTopoOrder");
        Footprint<IntOrderOnly>("ImmPkTopoOrder");

        Console.WriteLine("--- edge already in order (dependencies added before dependants)");
        Forward<IntTopoOrder>("ImmLeveledPkTopoOrder", full, edgeArr, E);
        Forward<IntOrderOnly>("ImmPkTopoOrder", full, edgeArr, E);
        Forward<KahnTopoOrder<int>>("Kahn oracle", full, edgeArr, 300);
    }

    private static TOrder Reversed<TOrder>() where TOrder : ITopologicalOrder<TOrder, int>
    {
        var o = TOrder.Empty;
        for (var i = 10_000 - 1; i >= 0; i--) o = o.Add(i);
        return o;
    }

    private static void Section<TOrder>(string name, ImmDag<int, int, NoPayload>[] dags, (int, int)[] edges, ImmDag<int, int, NoPayload> full, int perEdgeOps)
        where TOrder : ITopologicalOrder<TOrder, int>
    {
        var start = Reversed<TOrder>();
        Measure($"{name}: TryAddEdge per edge (persistent)", perEdgeOps, () =>
        {
            var o = start;
            for (var k = 0; k < perEdgeOps; k++) if (!o.TryAddEdge(dags[k], edges[k].Item1, edges[k].Item2, out o, out _)) throw new Exception();
        });
        Measure($"{name}: TryAddEdges, all {edges.Length:N0} edges as one batch", edges.Length, () =>
        {
            if (!start.TryAddEdges(full, edges, out _, out _)) throw new Exception();
        });
    }

    private static void Forward<TOrder>(string name, ImmDag<int, int, NoPayload> full, (int, int)[] edges, int ops) where TOrder : ITopologicalOrder<TOrder, int>
    {
        var o = TOrder.Empty;
        for (var i = 0; i < 10_000; i++) o = o.Add(i);
        if (o is IntTopoOrder lv) o = (TOrder)(object)lv.Tighten(full); // ranks and levels already agree: the steady state of incremental use
        Measure($"{name}: TryAddEdge", ops, () => { for (var k = 0; k < ops; k++) o.TryAddEdge(full, edges[k].Item1, edges[k].Item2, out _, out _); });
        Measure($"{name}: enumerate order", o.Count, () => { long s = 0; foreach (var n in o) s += n; if (s < 0) throw new Exception(); });
    }

    private static void Footprint<TOrder>(string name) where TOrder : ITopologicalOrder<TOrder, int>
    {
        var before = GC.GetTotalMemory(true);
        var o = TOrder.Empty;
        for (var i = 1; i <= 1_000_000; i++) o = o.Add(i);
        var after = GC.GetTotalMemory(true);
        Console.WriteLine($"{name,-58} {(after - before) / 1_000_000.0,12:F1} B/node retained");
        GC.KeepAlive(o);
    }

    private static void Measure(string name, int ops, Action body)
    {
        body();
        var best = double.MaxValue; long bytes = 0;
        for (var r = 0; r < 3; r++)
        {
            GC.Collect();
            var a = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            body();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds);
            bytes = GC.GetAllocatedBytesForCurrentThread() - a;
        }
        Console.WriteLine($"{name,-58} {best / ops,12:F1} ns/op {(double)bytes / ops,12:F1} B/op");
    }
}

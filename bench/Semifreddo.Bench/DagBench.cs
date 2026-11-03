using System.Diagnostics;
using Semifreddo;

/// <summary>Focused ImmDag costs. Run: dotnet run -c Release --project bench/Semifreddo.Bench -- dag</summary>
internal static class DagBench
{
    public static void Run()
    {
        const int NN = 100_000, EE = 200_000;
        var er = new Random(7);
        var edges = new (int, int)[EE];
        for (var i = 0; i < EE; i++) { var a = er.Next(NN - 1); var b = a + 1 + er.Next(Math.Min(50, NN - a - 1)); edges[i] = (a, b); }
        var probes = Enumerable.Range(0, NN).Select(_ => er.Next(NN)).ToArray();

        ImmDag<int, int, NoPayload> dag = default;
        Measure("build: nodes only (100k AddNode)", NN, () => { dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < NN; i++) s.AddNode(i, 1_000_000_000 - i); }, new DagBatchOptions { SkipCycleCheck = true }); });
        var nodesOnly = dag;
        Measure("build: + 200k AddEdge, unchecked", EE, () => { dag = nodesOnly.Mutate(edges, static (s, es) => { foreach (var (a, b) in es) s.AddEdge(a, b); }, new DagBatchOptions { SkipCycleCheck = true }); });
        Measure("build: + 200k AddEdge, checked (cycle DFS)", EE, () => { dag = nodesOnly.Mutate(edges, static (s, es) => { foreach (var (a, b) in es) s.AddEdge(a, b); }); });
        Measure("lookup: alt key -> id -> successor count", NN, () => { long s = 0; foreach (var p in probes) if (dag.TryGetNodeByAlternateKey(1_000_000_000 - p, out var id)) s += dag.Successors(id).Count; GC.KeepAlive(s); });
        Measure("lookup: ContainsEdge", EE, () => { var c = 0; foreach (var (a, b) in edges) if (dag.ContainsEdge(a, b)) c++; GC.KeepAlive(c); });
    }

    private static void Measure(string name, int ops, Action body)
    {
        body();
        var best = double.MaxValue; long bytes = 0;
        for (var r = 0; r < 5; r++)
        {
            GC.Collect();
            var a = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            body();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds);
            bytes = GC.GetAllocatedBytesForCurrentThread() - a;
        }
        Console.WriteLine($"{name,-50} {best / ops,9:F1} ns/op {(double)bytes / ops,9:F1} B/op");
    }
}

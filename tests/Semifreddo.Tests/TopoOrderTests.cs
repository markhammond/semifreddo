using Xunit;

namespace Semifreddo.Tests;

public class TopoOrderTests
{
    private static void AssertValid(ImmDag<int, int, NoPayload> dag, IntTopoOrder order)
    {
        Assert.Equal(dag.NodeCount, order.Count);
        foreach (var n in dag)
        {
            Assert.True(order.TryGetRank(n.Id, out var ru));
            foreach (var s in n.Successors) { Assert.True(order.TryGetRank(s, out var rv)); Assert.True(ru < rv, $"{n.Id}->{s} ranks {ru} >= {rv}"); }
        }
        var prev = long.MinValue; var count = 0;
        foreach (var n in order) { Assert.True(order.TryGetRank(n, out var r)); Assert.True(r > prev); prev = r; count++; }
        Assert.Equal(order.Count, count);
    }

    private static bool Reaches(ImmDag<int, int, NoPayload> dag, int from, int to)
    {
        var seen = new HashSet<int> { from };
        var stack = new Stack<int>([from]);
        while (stack.Count > 0) { var x = stack.Pop(); if (x == to) return true; foreach (var s in dag.Successors(x)) if (seen.Add(s)) stack.Push(s); }
        return false;
    }

    private static void AssertCycle(ImmDag<int, int, NoPayload> graphWithEdges, int[]? cycle)
    {
        Assert.NotNull(cycle); // the failure contract: a cycle is always reported
        Assert.Equal(cycle[0], cycle[^1]);
        for (var i = 0; i + 1 < cycle.Length; i++) Assert.True(graphWithEdges.ContainsEdge(cycle[i], cycle[i + 1]), $"{cycle[i]}->{cycle[i + 1]} not an edge");
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(2, 2000)]
    public void SingleEdges_RandomDirections_MatchReachability(int seed, int nodes)
    {
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(nodes, static (s, n) => { for (var i = 0; i < n; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(nodes, static (t, n) => { for (var i = n - 1; i >= 0; i--) t.Add(i); }); // reversed: forces reordering
        var cycles = 0;
        for (var k = 0; k < nodes * 3; k++)
        {
            int u = rnd.Next(nodes), v = rnd.Next(nodes);
            if (u == v || dag.ContainsEdge(u, v)) continue;
            var expectCycle = Reaches(dag, v, u);
            var withEdge = dag.Mutate((u, v), static (s, e) => s.AddEdge(e.u, e.v), new DagBatchOptions { SkipCycleCheck = true });
            var ok = order.TryAddEdge(withEdge, u, v, out var next, out var cycle);
            Assert.Equal(!expectCycle, ok);
            if (ok) { dag = withEdge; order = next; }
            else { cycles++; Assert.True(next.IsSameAs(order)); AssertCycle(withEdge, cycle); }
            if (k % 97 == 0) AssertValid(dag, order);
        }
        AssertValid(dag, order);
        Assert.True(cycles > 0);
    }

    [Theory]
    [InlineData(3, 5, 120)]    // small batches: per-edge PK with pending edges hidden
    [InlineData(4, 120, 600)]  // large batches: window re-sort
    public void Batches_BothStrategies_MatchKahn(int seed, int maxBatch, int nodes)
    {
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(nodes, static (s, n) => { for (var i = 0; i < n; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(nodes, static (t, n) => { for (var i = 0; i < n; i++) t.Add(i); });
        int failures = 0, successes = 0;
        for (var b = 0; b < 150; b++)
        {
            var edges = new List<(int, int)>();
            var k = 1 + rnd.Next(maxBatch);
            for (var i = 0; i < k; i++)
            {
                int u = rnd.Next(nodes), v = rnd.Next(nodes);
                if (u == v) continue;
                if (rnd.Next(100) < 97 && rnd.Next(2) == 0) (u, v) = (Math.Min(u, v), Math.Max(u, v)); // mostly "id-forward", rest random
                edges.Add((u, v));
            }
            var batch = edges.ToArray();
            var withEdges = dag.Mutate(batch, static (s, es) => { foreach (var (u, v) in es) s.AddEdge(u, v); }, new DagBatchOptions { SkipCycleCheck = true });
            var acyclic = withEdges.TryMutate(0, static (_, _) => { }, out _, out _) && IsAcyclic(withEdges);
            var ok = order.TryAddEdges(withEdges, batch, out var next, out var cycle);
            Assert.Equal(acyclic, ok);
            if (ok) { successes++; dag = withEdges; order = next; AssertValid(dag, order); }
            else { failures++; Assert.True(next.IsSameAs(order)); AssertCycle(withEdges, cycle); }
        }
        Assert.True(failures > 0 && successes > 0, $"{successes} ok / {failures} cyclic");
    }

    private static bool IsAcyclic(ImmDag<int, int, NoPayload> d)
    {
        var indeg = d.ToDictionary(n => n.Id, n => n.Predecessors.Count);
        var q = new Queue<int>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var seen = 0;
        while (q.Count > 0) { var n = q.Dequeue(); seen++; foreach (var s in d.Successors(n)) if (--indeg[s] == 0) q.Enqueue(s); }
        return seen == d.NodeCount;
    }

    [Fact]
    public void RemoveReplaceAndCompaction_KeepOrderValid()
    {
        var rnd = new Random(9);
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 400; i++) s.AddNode(i, i); for (var i = 0; i < 399; i++) s.AddEdge(399 - i, 398 - i); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(dag, static (t, d) => { foreach (var n in d) t.Add(n.Id); });
        Assert.True(order.TryAddEdges(dag, dag.SelectMany(n => n.Successors.Select(s => (n.Id, s))).ToArray(), out order, out _));
        AssertValid(dag, order);
        for (var k = 0; k < 300; k++)
        {
            var victim = dag.ElementAt(rnd.Next(dag.NodeCount)).Id;
            dag = dag.RemoveNode(victim);
            order = order.Remove(victim);
        }
        AssertValid(dag, order); // removals never invalidate an order; tombstones compacted along the way
        var a = dag.First().Id;
        var renamed = order.Replace(a, 10_000);
        Assert.False(renamed.Contains(a));
        Assert.True(renamed.TryGetRank(10_000, out var r1) && order.TryGetRank(a, out var r0) && r1 == r0);
    }

    [Fact]
    public void InsideDagBatch_UsingAddedEdges()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 10; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
        var ot = IntTopoOrder.Empty.Mutate(0, static (t, _) => { for (var i = 9; i >= 0; i--) t.Add(i); }).ToTransient();

        var next = dag.Mutate(ot, static (s, t) =>
        {
            s.AddNode(10, 10); t.Add(10);
            s.AddEdge(0, 5); s.AddEdge(5, 10); s.AddEdge(2, 3);
            if (!t.TryAddEdges(s, s.AddedEdges, out var cycle)) throw new DagCycleException<int>(cycle);
        }, new DagBatchOptions { SkipCycleCheck = true });
        var order = ot.ToPersistent();
        AssertValid(next, order);

        var bad = ot; // same transient, now attempt a cycle
        Assert.Throws<DagCycleException<int>>(() => next.Mutate(bad, static (s, t) =>
        {
            s.AddEdge(10, 0);
            if (!t.TryAddEdges(s, s.AddedEdges, out var cycle)) throw new DagCycleException<int>(cycle);
        }, new DagBatchOptions { SkipCycleCheck = true }));
        bad.Reset(order); // roll back the order alongside the (untouched) dag
        AssertValid(next, bad.ToPersistent());
    }

    [Fact]
    public void Sentinel_IsRejected_AndZeroIsAValidId()
    {
        Assert.Throws<ArgumentException>(() => IntTopoOrder.Empty.Add(int.MinValue));
        var o = IntTopoOrder.Empty.Add(0).Add(1);
        Assert.True(o.Precedes(0, 1));
        Assert.Throws<ArgumentException>(() => o.Replace(0, int.MinValue));
    }

    [Fact]
    public void ReferenceNodes_UseNullSentinel_ForHoles()
    {
        var o = ImmPkTopoOrder<string, DefaultSentinel<string>>.Empty;
        var names = Enumerable.Range(0, 200).Select(i => "n" + i).ToArray();
        foreach (var n in names) o = o.Add(n);
        for (var i = 1; i < 199; i += 2) o = o.Remove(names[i]); // interior holes, then compaction
        Assert.Equal(names.Where((_, i) => i % 2 == 0 || i == 199), o.ToArray());
        Assert.Throws<ArgumentException>(() => o.Add(null!));
        Assert.True(o.Precedes("n0", "n198"));
    }

    [Fact]
    public void Probe_FailurePath_IsAllocationFree()
    {
        const int n = 1000;
        var chain = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < n; i++) s.AddNode(i, i); for (var i = 0; i + 1 < n; i++) s.AddEdge(i, i + 1); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(0, static (t, _) => { for (var i = 0; i < n; i++) t.Add(i); });
        var cyclic = chain.Mutate(static s => s.AddEdge(n - 1, 0), new DagBatchOptions { SkipCycleCheck = true });
        var back = Enumerable.Range(0, 40).Select(i => (n - 1 - i, i)).ToArray(); // > 32 late edges: window re-sort path
        var cyclicMany = chain.Mutate(back, static (s, es) => { foreach (var (u, v) in es) s.AddEdge(u, v); }, new DagBatchOptions { SkipCycleCheck = true });
        var t = order.ToTransient();

        Assert.False(order.TryAddEdge(cyclic, n - 1, 0, out _));
        Assert.False(t.TryAddEdge(cyclic, n - 1, 0));
        Assert.False(order.TryAddEdges(cyclicMany, back, out _));

        Assert.Equal(0, Alloc.SteadyState(() => order.TryAddEdge(cyclic, n - 1, 0, out _)));        // persistent, PK
        Assert.Equal(0, Alloc.SteadyState(() => { t.Reset(order); t.TryAddEdge(cyclic, n - 1, 0); })); // transient, PK
        var window = Alloc.SteadyState(() => order.TryAddEdges(cyclicMany, back, out _));          // persistent, window re-sort
        Assert.True(window == 0, $"{window} B on the window-resort failure path");
    }

    [Fact]
    public async Task Bisect_OnAnotherThread_FromImmutableInputs()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 50; i++) s.AddNode(i, i); for (var i = 0; i < 49; i++) s.AddEdge(i, i + 1); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(dag, static (t, d) => { foreach (var n in d) t.Add(n.Id); });
        Assert.True(order.TryAddEdges(dag, dag.SelectMany(n => n.Successors.Select(s => (n.Id, s))).ToArray(), out order));
        (int, int)[] batch = [(3, 10), (5, 40), (30, 2), (7, 9)]; // (30, 2) closes 2 -> ... -> 30 -> 2

        // hot path: probe only, keep going on failure
        var withBatch = dag.Mutate(batch, static (s, es) => { foreach (var (u, v) in es) s.AddEdge(u, v); }, new DagBatchOptions { SkipCycleCheck = true });
        Assert.False(order.TryAddEdges(withBatch, batch, out var unchanged));
        Assert.True(unchanged.IsSameAs(order));

        // diagnosis on a worker: persistent values are immutable, so sharing them across threads is safe
        var (culprit, cycle) = await Task.Run(() =>
        {
            var g = dag;
            var o = order;
            foreach (var e in batch)
            {
                g = g.Mutate(e, static (s, x) => s.AddEdge(x.Item1, x.Item2), new DagBatchOptions { SkipCycleCheck = true });
                if (!o.TryAddEdge(g, e.Item1, e.Item2, out var next, out var c)) return (e, c);
                o = next;
            }
            return ((0, 0), Array.Empty<int>());
        });
        Assert.Equal((30, 2), culprit);
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.Contains((30, 2), cycle.Zip(cycle.Skip(1)));
    }

    // ------------------------------------------------------------------ levels

    private static (ImmDag<int, int, NoPayload> Dag, IntTopoOrder Order) Build(int nodes, params (int, int)[] edges)
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate((nodes, edges), static (s, a) => { for (var i = 0; i < a.nodes; i++) s.AddNode(i, i); foreach (var (u, v) in a.edges) s.AddEdge(u, v); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Mutate(nodes, static (t, n) => { for (var i = n - 1; i >= 0; i--) t.Add(i); });
        Assert.True(order.TryAddEdges(dag, edges, out order));
        return (dag, order);
    }

    [Fact]
    public void Levels_GroupDiamond_AndCopyByLevel()
    {
        // 0 -> {1, 2} -> 3, and 4 isolated
        var (dag, order) = Build(5, (0, 1), (0, 2), (1, 3), (2, 3));
        Assert.Equal(3, order.LevelCount);
        Assert.Equal(2, order.MaxLevel);
        Assert.Equal([2, 2, 1], new[] { order.LevelWidth(0), order.LevelWidth(1), order.LevelWidth(2) });
        var groups = order.ToLevels();
        Assert.Equal([0, 4], groups[0].Order().ToArray());
        Assert.Equal([1, 2], groups[1].Order().ToArray());
        Assert.Equal([3], groups[2]);
        foreach (var g in groups) foreach (var a in g) foreach (var b in g) Assert.False(dag.ContainsEdge(a, b)); // no edge inside a group
        foreach (var g in groups) for (var i = 1; i < g.Length; i++) Assert.True(order.Precedes(g[i - 1], g[i])); // topo order within group

        Span<int> nodes = stackalloc int[5];
        Span<int> starts = stackalloc int[4];
        order.CopyByLevel(nodes, starts);
        Assert.Equal([0, 2, 4, 5], starts.ToArray());
    }

    [Fact]
    public void Levels_InsertOnly_AreExact_RemovalsLoosen_TightenRestores()
    {
        var (dag, order) = Build(4, (0, 1), (1, 2), (2, 3));
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4).Select(order.Level).ToArray()); // raise-only fixpoint = longest path
        var cut = dag.RemoveEdge(1, 2);
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4).Select(order.Level).ToArray()); // still valid, now loose
        var tight = order.Tighten(cut);
        Assert.Equal([0, 1, 0, 1], Enumerable.Range(0, 4).Select(tight.Level).ToArray());
        Assert.Equal(2, tight.LevelCount);
        Assert.Equal(2, tight.Remove(3).LevelCount);            // node 1 still occupies level 1
        Assert.Equal(1, tight.Remove(3).Remove(1).LevelCount);  // trailing empty level dropped
    }

    [Fact]
    public void FastPath_StillRaisesLevel_WhenOnlyTheLevelDisagrees()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { s.AddNode(0, 0); s.AddNode(1, 1); s.AddEdge(0, 1); }, new DagBatchOptions { SkipCycleCheck = true });
        var order = IntTopoOrder.Empty.Add(0).Add(1); // ranks already agree with 0 -> 1, levels do not (both 0)
        Assert.True(order.TryAddEdge(dag, 0, 1, out var next));
        Assert.Equal(1, next.Level(1));
        Assert.True(order.TryAddEdges(dag, [(0, 1)], out next));
        Assert.Equal(1, next.Level(1));
    }

    [Fact]
    public void BottomLevels_GiveCriticalPath()
    {
        // 0 -> 1 -> 3, 0 -> 2 -> 3 ; node 2 is expensive
        var (dag, order) = Build(4, (0, 1), (0, 2), (1, 3), (2, 3));
        double[] cost = [1, 1, 10, 2];
        var bottom = new Dictionary<int, double>();
        var critical = order.ComputeBottomLevels(dag, n => cost[n], bottom);
        Assert.Equal(13, critical);                 // 0 -> 2 -> 3
        Assert.Equal(12, bottom[2]);
        Assert.Equal(3, bottom[1]);
        Assert.True(bottom[2] > bottom[1]);         // schedule 2 before 1 when both are ready
    }

    [Fact]
    public void WithDetails_And_CopyByLevel_ReportPredecessorCounts()
    {
        var (dag, order) = Build(5, (0, 1), (0, 2), (1, 3), (2, 3));
        var seen = new List<TopoNodeInfo<int>>();
        foreach (var info in order.WithDetails(dag)) seen.Add(info);
        Assert.Equal(order.ToArray(), seen.Select(i => i.Node).ToArray());
        Assert.All(seen, i => { Assert.Equal(dag.Predecessors(i.Node).Count, i.PredecessorCount); Assert.Equal(order.Level(i.Node), i.Level); });
        Assert.Equal(2, seen.Single(i => i.Node == 3).PredecessorCount);

        var nodes = new int[5]; var starts = new int[order.LevelCount + 1]; var preds = new int[5];
        order.CopyByLevel(dag, nodes, starts, preds);
        for (var i = 0; i < 5; i++) Assert.Equal(dag.Predecessors(nodes[i]).Count, preds[i]);
    }

    [Fact]
    public void Levels_ManyPathsOfDifferentLengths_RaiseEachNodeOnce()
    {
        // a ladder: every node i links to i+1 and i+2 — exponentially many paths; depth-first raising would explode
        const int n = 3000;
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < n; i++) s.AddNode(i, i); for (var i = 0; i + 1 < n; i++) { s.AddEdge(i, i + 1); if (i + 2 < n) s.AddEdge(i, i + 2); } }, new DagBatchOptions { SkipCycleCheck = true });
        var edges = dag.SelectMany(x => x.Successors.Select(t => (x.Id, t))).OrderByDescending(e => e.Id).ToArray(); // worst order
        var order = IntTopoOrder.Empty.Mutate(0, static (t, _) => { for (var i = 0; i < n; i++) t.Add(i); });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var e in edges) Assert.True(order.TryAddEdge(dag, e.Item1, e.Item2, out order));
        Assert.True(sw.Elapsed.TotalSeconds < 20, $"{sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal(n - 1, order.Level(n - 1));
        Assert.Equal(n, order.LevelCount);
    }

    // One generic routine over the CRTP transient base drives both variants and freezes to the right persistent type.
    private static TOrder BuildChain<TT, TOrder, TEntry>(TT t, ImmDag<int, int, NoPayload> dag, int n)
        where TT : TopoTransient<TT, TOrder, int, MinValueSentinel<int>, TEntry>, new()
        where TOrder : struct, ITopoOrderStorage<TOrder, int, MinValueSentinel<int>, TEntry>
        where TEntry : struct, ITopoEntry
    {
        for (var i = n - 1; i >= 0; i--) Assert.True(t.Add(i));                 // reverse: every edge disagrees
        for (var i = 0; i + 1 < n; i++) Assert.True(t.TryAddEdge(dag, i, i + 1));
        var cyclic = dag.Mutate(n, static (s, k) => s.AddEdge(k - 1, 0), new DagBatchOptions { SkipCycleCheck = true });
        Assert.False(t.TryAddEdge(cyclic, n - 1, 0));                           // probe failure path
        return t.ToPersistent();
    }

    [Fact]
    public void CrtpTransientBase_DrivesBothVariants()
    {
        const int n = 50;
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) =>
        {
            for (var i = 0; i < k; i++) s.AddNode(i, i);
            for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1);
        }, new DagBatchOptions { SkipCycleCheck = true });
        var plain = BuildChain<IntOrderOnly.Transient, IntOrderOnly, NoLevels>(IntOrderOnly.Empty.ToTransient(), dag, n);
        var leveled = BuildChain<IntTopoOrder.Transient, IntTopoOrder, WithLevels>(IntTopoOrder.Empty.ToTransient(), dag, n);
        Assert.Equal(Enumerable.Range(0, n), plain);
        Assert.Equal(Enumerable.Range(0, n), leveled);
        Assert.Equal(Enumerable.Range(0, n), Enumerable.Range(0, n).Select(leveled.Level));
    }
}

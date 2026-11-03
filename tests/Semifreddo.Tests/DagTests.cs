using Xunit;

namespace Semifreddo.Tests;

public class DagTests
{
    private sealed class Model
    {
        public readonly Dictionary<int, HashSet<int>> Out = [];
        public readonly Dictionary<int, HashSet<int>> In = [];
        public readonly Dictionary<int, int> Alt = [];
        public readonly Dictionary<int, int> ByAlt = [];
        public int Edges;

        public Model Clone()
        {
            var m = new Model();
            foreach (var (k, v) in Out) m.Out[k] = [.. v];
            foreach (var (k, v) in In) m.In[k] = [.. v];
            foreach (var (k, v) in Alt) m.Alt[k] = v;
            foreach (var (k, v) in ByAlt) m.ByAlt[k] = v;
            m.Edges = Edges;
            return m;
        }

        public bool IsAcyclic() // Kahn
        {
            var indeg = Out.Keys.ToDictionary(k => k, k => In[k].Count);
            var q = new Queue<int>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var seen = 0;
            while (q.Count > 0) { var n = q.Dequeue(); seen++; foreach (var s in Out[n]) if (--indeg[s] == 0) q.Enqueue(s); }
            return seen == Out.Count;
        }
    }

    private static void AssertEqual(Model m, ImmDag<int, int, NoPayload> d)
    {
        Assert.Equal(m.Out.Count, d.NodeCount);
        Assert.Equal(m.Edges, d.EdgeCount);
        foreach (var v in d)
        {
            Assert.Equal(m.Alt[v.Id], v.AlternateKey);
            Assert.True(d.TryGetNodeByAlternateKey(v.AlternateKey, out var id) && id == v.Id);
            Assert.True(m.Out[v.Id].SetEquals(v.Successors));
            Assert.True(m.In[v.Id].SetEquals(v.Predecessors));
            Assert.Equal(v.Successors.Count, v.Successors.Count());
        }
    }

    [Theory]
    [InlineData(1, 0.0)]  // forward edges only: always acyclic
    [InlineData(2, 0.03)] // some back edges: some batches must fail
    [InlineData(3, 0.15)]
    public void RandomBatches_MatchModel_AndCyclicBatchesRollBack(int seed, double backEdgeRate)
    {
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty;
        var model = new Model();
        var history = new List<(ImmDag<int, int, NoPayload>, Model)>();
        int nextId = 0, failures = 0;

        for (var batch = 0; batch < 300; batch++)
        {
            var trial = model.Clone();
            var ops = new List<(int Kind, int A, int B)>();
            var nOps = 1 + rnd.Next(40);
            for (var k = 0; k < nOps; k++)
            {
                var r = rnd.Next(100);
                var ids = trial.Out.Keys.ToArray();
                if (r < 25 || ids.Length < 2)
                {
                    var id = nextId++;
                    ops.Add((0, id, 1_000_000 + id)); // int node id, int alternate key: different domains
                    trial.Out[id] = []; trial.In[id] = []; trial.Alt[id] = 1_000_000 + id; trial.ByAlt[1_000_000 + id] = id;
                }
                else if (r < 80)
                {
                    int a = ids[rnd.Next(ids.Length)], b = ids[rnd.Next(ids.Length)];
                    if (a == b) continue;
                    if ((a > b) != (rnd.NextDouble() < backEdgeRate)) (a, b) = (b, a); // forward unless chosen as back edge
                    ops.Add((1, a, b));
                    if (trial.Out[a].Add(b)) { trial.In[b].Add(a); trial.Edges++; }
                }
                else if (r < 92 && trial.Edges > 0)
                {
                    var a = ids[rnd.Next(ids.Length)];
                    if (trial.Out[a].Count == 0) continue;
                    var b = trial.Out[a].First();
                    ops.Add((2, a, b));
                    trial.Out[a].Remove(b); trial.In[b].Remove(a); trial.Edges--;
                }
                else
                {
                    var a = ids[rnd.Next(ids.Length)];
                    ops.Add((3, a, 0));
                    foreach (var s in trial.Out[a]) trial.In[s].Remove(a);
                    foreach (var p in trial.In[a]) trial.Out[p].Remove(a);
                    trial.Edges -= trial.Out[a].Count + trial.In[a].Count;
                    trial.Out.Remove(a); trial.In.Remove(a); trial.ByAlt.Remove(trial.Alt[a]); trial.Alt.Remove(a);
                }
            }

            var ok = dag.TryMutate(ops, static (s, os) =>
            {
                foreach (var (kind, a, b) in os)
                {
                    switch (kind)
                    {
                        case 0: Assert.True(s.AddNode(a, b)); break;
                        case 1: s.AddEdge(a, b); break;
                        case 2: Assert.True(s.RemoveEdge(a, b)); break;
                        default: Assert.True(s.RemoveNode(a)); break;
                    }
                }
            }, out var next, out var cycle);

            Assert.Equal(trial.IsAcyclic(), ok);
            if (ok) { dag = next; model = trial; }
            else
            {
                failures++;
                Assert.True(next.IsSameAs(dag)); // rollback: untouched
                Assert.NotNull(cycle); // the failure contract: a cycle is always reported
                Assert.Equal(cycle[0], cycle[^1]);
                for (var i = 0; i + 1 < cycle.Length; i++) Assert.Contains(cycle[i + 1], trial.Out[cycle[i]]); // a real cycle
            }
            if (batch % 25 == 0) history.Add((dag, model.Clone()));
        }
        AssertEqual(model, dag);
        foreach (var (hd, hm) in history) AssertEqual(hm, hd);
        if (backEdgeRate == 0) Assert.Equal(0, failures);
        else Assert.True(failures > 0);
    }

    [Fact]
    public void HighDegree_SwitchesToSet_AndBack()
    {
        var dag = ImmDag<int, string, NoPayload>.Empty.Mutate(static s =>
        {
            for (var i = 0; i <= 100; i++) s.AddNode(i, "n" + i);
            for (var i = 1; i <= 100; i++) s.AddEdge(0, i);
        });
        var succ = dag.Successors(0);
        Assert.Equal(100, succ.Count);
        Assert.False(succ.TryGetSpan(out _));                 // CHAMP-backed
        Assert.True(dag.Predecessors(50).TryGetSpan(out var p) && p.SequenceEqual([0]));
        for (var i = 1; i <= 95; i++) dag = dag.RemoveEdge(0, i);
        Assert.True(dag.Successors(0).TryGetSpan(out var rest)); // back inline below the hysteresis threshold
        Assert.Equal([96, 97, 98, 99, 100], rest.ToArray().Order().ToArray());
        Assert.Equal(5, dag.EdgeCount);
        dag = dag.RemoveNode(0);
        Assert.Equal(0, dag.EdgeCount);
        Assert.True(dag.Predecessors(96).IsEmpty);
        Assert.False(dag.TryGetNodeByAlternateKey("n0", out _));
    }

    [Fact]
    public void Bisect_FailedBatch_FindsOffendingEdge()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 50; i++) s.AddNode(i, -i); for (var i = 0; i < 49; i++) s.AddEdge(i, i + 1); });
        (int, int)[] batch = [(3, 10), (5, 40), (20, 2), (7, 9)]; // (20, 2) closes 2 -> ... -> 20 -> 2
        Assert.False(dag.TryMutate(batch, static (s, es) => { foreach (var (a, b) in es) s.AddEdge(a, b); }, out var same, out var cycle));
        Assert.True(same.IsSameAs(dag));
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.Contains((20, 2), cycle.Zip(cycle.Skip(1))); // reported cycle runs through the culprit edge

        // the user's bisect: re-apply one at a time on the last good version
        var good = dag;
        (int, int)? culprit = null;
        foreach (var e in batch)
        {
            if (good.TryMutate(e, static (s, x) => s.AddEdge(x.Item1, x.Item2), out var next, out _)) good = next;
            else { culprit = e; break; }
        }
        Assert.Equal((20, 2), culprit);
        Assert.Throws<DagCycleException<int>>(() => dag.AddEdge(20, 2));
        Assert.Throws<DagCycleException<int>>(() => dag.AddEdge(7, 7)); // a self-loop is a cycle
        Assert.Throws<KeyNotFoundException>(() => dag.AddEdge(7, 999));
    }

    [Fact]
    public void AddNode_RefusesATakenIdOrAlternateKey_AndLeavesNoTrace()
    {
        var t = ImmDag<int, int, int>.Empty.Mutate(static s => { s.AddNode(1, 10, 100); s.AddNode(2, 20, 200); }).ToTransient();
        Assert.False(t.AddNode(1, 30, 300));                 // id taken
        Assert.False(t.AddNode(3, 20, 300));                 // alternate key taken: the id inserted first is undone
        Assert.True(t.AddedNodes.IsEmpty);
        var d = t.ToPersistent();
        Assert.Equal(2, d.NodeCount);
        Assert.False(d.ContainsNode(3));
        Assert.True(d.TryGetValue(1, out var v) && v == 100);
        Assert.True(d.TryGetNodeByAlternateKey(20, out var id) && id == 2);
        Assert.True(t.AddNode(3, 30, 300));                  // and the id is free again
        Assert.Equal([3], t.AddedNodes.ToArray());
        Assert.Equal(3, t.ToPersistent().NodeCount);

        var g = ImmDag<int, int, int>.Empty.Mutate(static s => { for (var i = 0; i < 64; i++) s.AddNode(i, i, i); });
        var same = g.Mutate(static s => { s.AddNode(5, 99, 0); s.AddNode(99, 5, 0); });   // refused, both ways
        Assert.True(same.IsSameAs(g));                       // nothing changed: the same version, nothing copied
    }

    [Fact]
    public void ASelfLoop_InABatch_IsReportedAtItsEnd_WithoutAnException()
    {
        var g = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (int i = 0; i < 4; i++) s.AddNode(i, i); s.AddEdge(0, 1); });
        int thread = Environment.CurrentManagedThreadId, thrown = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> count =
            (_, _) => { if (Environment.CurrentManagedThreadId == thread) thrown++; };
        AppDomain.CurrentDomain.FirstChanceException += count;
        try
        {
            Assert.False(g.TryMutate(0, static (s, _) => { s.AddEdge(1, 2); Assert.True(s.AddEdge(2, 2)); s.AddEdge(2, 3); }, out var same, out var cycle));
            Assert.True(same.IsSameAs(g));
            Assert.Equal([2, 2], cycle);

            var skip = new DagBatchOptions { SkipCycleCheck = true };
            Assert.False(g.TryMutate(0, static (s, _) => s.AddEdge(3, 3), skip, out same, out cycle));
            Assert.True(same.IsSameAs(g));
            Assert.Equal([3, 3], cycle);

            var order = ImmPkTopoOrder.CreateWithMinValueSentinel<int>();
            for (int i = 0; i < 4; i++) order = order.Add(i);
            var before = order;
            Assert.False(g.TryMutate(ref order, 0, static (s, _) => { s.AddEdge(1, 2); s.AddEdge(1, 1); }, out same, out cycle));
            Assert.True(same.IsSameAs(g) && order.IsSameAs(before));
            Assert.Equal([1, 1], cycle);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= count; }
        Assert.Equal(0, thrown);

        var e = Assert.Throws<DagCycleException<int>>(() => g.Mutate(static s => s.AddEdge(0, 0)));   // the throwing form, at the end
        Assert.Equal([0, 0], e.Cycle);
        Assert.Throws<DagCycleException<int>>(() => g.Mutate(static s => s.AddEdge(0, 0), new DagBatchOptions { SkipCycleCheck = true }));
        Assert.Throws<KeyNotFoundException>(() => g.Mutate(static s => s.AddEdge(9, 9)));
    }

    [Fact]
    public void Transient_KeepsASelfLoopOut_ButCyclicUntilReset()
    {
        var g = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (int i = 0; i < 3; i++) s.AddNode(i, i); });
        var t = g.ToTransient();
        Assert.True(t.AddEdge(1, 1));
        Assert.False(t.ContainsEdge(1, 1));                  // never stored
        Assert.True(t.AddedEdges.IsEmpty);
        Assert.False(t.Validate());
        Assert.False(t.Validate(out var cycle));
        Assert.Equal([1, 1], cycle);
        Assert.False(t.TryToPersistent(out _));
        Assert.False(t.RemoveEdge(1, 1));                    // nothing to remove: the batch stays cyclic
        Assert.False(t.Validate());
        t.Reset(g);
        Assert.True(t.Validate());
        Assert.True(t.TryToPersistent(out var same));
        Assert.True(same.IsSameAs(g));
    }

    [Fact]
    public void Validate_WithoutTheCycle_AllocatesNothing()
    {
        var t = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (int i = 0; i < 40; i++) s.AddNode(i, i); for (int i = 0; i + 1 < 40; i++) s.AddEdge(i, i + 1); }).ToTransient();
        t.AddEdge(39, 0);
        Assert.False(t.Validate(out var cycle));
        Assert.Equal(41, cycle.Length);
        Assert.Equal(0, Alloc.SteadyState(() => t.Validate()));
        Assert.Equal(0, Alloc.SteadyState(() => t.TryToPersistent(out _)));
        Assert.True(Alloc.SteadyState(() => t.Validate(out _)) > 0);      // the cycle, 41 nodes
    }

    [Fact]
    public void Transient_ExposesAddedEdges_ForIncrementalConsumers()
    {
        var t = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 4; i++) s.AddNode(i, i); }).ToTransient();
        t.AddEdge(0, 1); t.AddEdge(1, 2); t.AddEdge(0, 1);
        Assert.Equal([(0, 1), (1, 2)], t.AddedEdges.ToArray());
        var d = t.ToPersistent();
        Assert.True(t.AddedEdges.IsEmpty);
        t.AddEdge(2, 0); // cycle
        Assert.Throws<DagCycleException<int>>(() => t.ToPersistent());
        t.Reset(d); // rollback
        Assert.Equal(2, t.ToPersistent().EdgeCount);
    }

    [Fact]
    public void Interlocked_ConcurrentEdgeAdds()
    {
        var shared = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 200; i++) s.AddNode(i, i); });
        Parallel.For(0, 8, w =>
        {
            for (var i = w; i < 199; i += 8) ImmInterlocked.Update(ref shared, static (d, k) => d.AddEdge(k, k + 1), i);
        });
        Assert.Equal(199, shared.EdgeCount);
    }

    [Fact]
    public void SkipCycleCheck_SkipsValidation_ForExternalOrdering()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 10; i++) s.AddNode(i, i); });
        int[] order = [.. Enumerable.Range(0, 10)]; // caller-maintained topological order: rank = id
        var next = dag.Mutate(order, static (s, rank) =>
        {
            s.AddEdge(1, 5); s.AddEdge(5, 9);
            foreach (var (a, b) in s.AddedEdges) Assert.True(rank[a] < rank[b]); // the caller's O(1) per-edge check
        }, new DagBatchOptions { SkipCycleCheck = true });
        Assert.Equal(2, next.EdgeCount);
    }
}

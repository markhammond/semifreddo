using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

public class DagBatchOptionsTests
{
    private static ImmDag<int, int, NoPayload> Chain(int n)
        => ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) => { for (var i = 0; i < k; i++) s.AddNode(i, i); for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1); });

    [Fact]
    public void SkipChangeLog_TheLogThrows_TheCycleCheckStillWorks()
    {
        var dag = Chain(4);
        var quiet = new DagBatchOptions { SkipChangeLog = true };
        var next = dag.Mutate(static s =>
        {
            s.AddNode(9, 9);
            s.AddEdge(3, 9);
            for (var which = 0; which < 3; which++) Assert.True(LogThrows(s, which), $"log accessor {which} answered");
        }, quiet);
        Assert.True(next.ContainsEdge(3, 9));

        Assert.Throws<DagCycleException<int>>(() => dag.Mutate(static s => s.AddEdge(3, 0), quiet));
        Assert.False(dag.TryMutate(0, static (s, _) => s.AddEdge(3, 1), quiet, out var same, out var cycle));
        Assert.True(same.IsSameAs(dag));
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.True(dag.Mutate(static s => s.AddEdge(0, 3), quiet).ContainsEdge(0, 3));   // a forward edge: fine
    }

    private static bool LogThrows(ImmDag<int, int, NoPayload>.Scope s, int which)
    {
        try
        {
            _ = which switch { 0 => s.AddedEdges.Length, 1 => s.RemovedNodes.Length, _ => s.UpdatedNodes.Length };
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [Fact]
    public void SkipCycleCheck_CommitsWithoutValidating()
    {
        var dag = Chain(3);
        var both = dag.Mutate(static s => s.AddEdge(2, 0), new DagBatchOptions { SkipCycleCheck = true, SkipChangeLog = true });
        Assert.False(Check.IsAcyclic(both, [0, 1, 2]));     // the caller's promise, unchecked: a cycle commits
        Assert.True(dag.TryMutate(0, static (s, _) => s.AddEdge(2, 0), new DagBatchOptions { SkipCycleCheck = true }, out var r, out _));
        Assert.True(r.ContainsEdge(2, 0));
    }

    [Fact]
    public void Transient_TrackChanges_SwitchesOnlyBetweenBatches()
    {
        var t = Chain(3).ToTransient();
        Assert.True(t.TrackChanges);
        t.TrackChanges = false;
        t.AddNode(5, 5);
        Assert.Throws<InvalidOperationException>(() => t.AddedNodes.Length);
        t.AddEdge(2, 5);
        Assert.Throws<InvalidOperationException>(() => t.TrackChanges = true);   // mid-batch
        var frozen = t.ToPersistent();                                           // validated from the seeds
        Assert.True(frozen.ContainsEdge(2, 5));
        t.TrackChanges = true;                                                   // between batches
        t.AddNode(6, 6);
        Assert.Equal([6], t.AddedNodes.ToArray());
        t.AddEdge(5, 0);
        Assert.Throws<DagCycleException<int>>(() => t.ToPersistent());
    }

    [Fact]
    public void SkipChangeLog_ABigBatch_GrowsNoLog()
    {
        // The log's capacity is kept for the thread's next batch (no allocation once warm), so its cost is the growth
        // on a thread's first big batch, and what it then keeps: measured on a fresh thread each time.
        var edges = Enumerable.Range(0, 20_000).Select(i => (i, i + 1)).ToArray();
        long FirstBatch(DagBatchOptions o)
        {
            long bytes = 0;
            var thread = new Thread(() =>
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                ImmDag<int, int, NoPayload>.Empty.Mutate(edges, static (s, es) =>
                {
                    for (var i = 0; i <= es.Length; i++) s.AddNode(i, i);
                    foreach (var (a, b) in es) s.AddEdge(a, b);
                }, o);
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            });
            thread.Start();
            thread.Join();
            return bytes;
        }
        long logged = FirstBatch(default), quiet = FirstBatch(new DagBatchOptions { SkipChangeLog = true });
        Assert.True(quiet < logged - 200_000, $"with the log {logged:N0} B, without {quiet:N0} B");
    }

    [Fact]
    public void WithAnOrder_TheConveniences_AndAnOrderOutOfStep()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty;
        var order = IntOrderOnly.Empty;
        dag = dag.AddNode(ref order, 1, 1, default).AddNode(ref order, 2, 2, default).AddNode(ref order, 3, 3, default);
        dag = dag.AddEdge(ref order, 3, 2).AddEdge(ref order, 2, 1);          // against insertion order: reordered
        Check.TopologicalOrder(dag, [1, 2, 3], order);
        var before = order;
        Assert.Throws<DagCycleException<int>>(() => dag.AddEdge(ref order, 1, 3));
        Assert.True(order.IsSameAs(before));
        dag = dag.RemoveNode(ref order, 2);
        Assert.Equal(2, order.Count);
        Check.TopologicalOrder(dag, [1, 3], order);

        var stale = IntOrderOnly.Empty.Add(1);
        Assert.Throws<ArgumentException>(() => dag.AddEdge(ref stale, 3, 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WithAnOrder_RandomBatches_KeepItInStep_AndAgreeWithThePlainCheck_OrderOnly(int seed) => RandomBatches(IntOrderOnly.Empty, seed);

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void WithAnOrder_RandomBatches_KeepItInStep_AndAgreeWithThePlainCheck_Levelled(int seed) => RandomBatches(IntTopoOrder.Empty, seed);

    private const int Ids = 16;

    private static void RandomBatches<TOrder>(TOrder order, int seed) where TOrder : ITopologicalOrder<TOrder, int>
    {
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty;
        int accepted = 0, refused = 0;
        for (var step = 0; step < 2000; step++)
        {
            var plan = new List<(int Kind, int A, int B, int C)>();
            for (var e = rnd.Next(1, 6); e > 0; e--) plan.Add((rnd.Next(7), rnd.Next(Ids), rnd.Next(Ids), rnd.Next(Ids)));
            var plainOk = dag.TryMutate(plan, Apply, out var plain, out _);
            var o = order;
            var ok = dag.TryMutate(ref o, plan, Apply, out var result, out var cycle);
            Assert.Equal(plainOk, ok);
            if (!ok)
            {
                Assert.True(o.IsSameAs(order) && result.IsSameAs(dag), "a refused batch changed something");
                Assert.Equal(cycle![0], cycle[^1]);
                refused++;
                continue;
            }
            var nodes = result.Select(n => n.Id).ToArray();
            Assert.Equal(plain.Select(n => n.Id).Order(), nodes.Order());
            Assert.Equal(nodes.Length, o.Count);
            Check.TopologicalOrder(result, nodes, o);
            if (o is IntTopoOrder levelled) Check.Levels(result, nodes, levelled);
            Check.Dag(result);
            dag = result;
            order = o;
            accepted++;
        }
        Assert.True(accepted > 500 && refused > 30, $"exercised: {accepted} accepted, {refused} refused");
    }

    private static void Apply(ImmDag<int, int, NoPayload>.Scope s, List<(int Kind, int A, int B, int C)> plan)
    {
        foreach (var (kind, a, b, c) in plan)
        {
            switch (kind)
            {
                case 0: case 1: if (!s.ContainsNode(a)) s.AddNode(a, a + 1000 * (b + 1)); break;   // alternate keys kept unique
                case 2: s.RemoveNode(a); break;
                case 3: case 4: if (a != b && s.ContainsNode(a) && s.ContainsNode(b)) s.AddEdge(a, b); break;   // any direction
                case 5: if (s.ContainsEdge(a, b)) s.RemoveEdge(a, b); else if (s.ContainsNode(a) && !s.ContainsNode(b)) s.ReplaceNode(a, b); break;
                default:                                                                    // swap two ids through a third
                    if (a != b && s.ContainsNode(a) && s.ContainsNode(b) && !s.ContainsNode(c))
                    {
                        s.ReplaceNode(a, c);
                        s.ReplaceNode(b, a);
                        s.ReplaceNode(c, b);
                    }
                    break;
            }
        }
    }
}

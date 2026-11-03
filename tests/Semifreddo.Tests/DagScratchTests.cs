using Semifreddo.Internal;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>A pooled transient keeps its scratch between batches, but no more than <see cref="Scratch.Limit"/> of
/// it: a hash set clears in time proportional to its capacity, so one large batch must not slow every small one.</summary>
public class DagScratchTests
{
    [Fact]
    public void ALargeBatch_LeavesBoundedScratch_InThePool()
    {
        const int n = 50_000;
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) =>
        {
            for (var i = 0; i < k; i++) s.AddNode(i, i);
            for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1);    // one chain: the cycle check visits every node
        });
        dag = dag.Mutate(n, static (s, k) => { for (var i = 0; i < k; i += 2) s.RemoveEdge(i, i + 1); });   // a large log, and touched set
        var pooled = ImmDag<int, int, NoPayload>.Transient.Pooled;
        Assert.NotNull(pooled);
        Assert.True(pooled.ScratchCapacity <= Scratch.Limit, $"pooled scratch {pooled.ScratchCapacity}");
        Assert.Equal(n / 2 - 1, dag.EdgeCount);

        var next = dag.AddEdge(0, 1);                                 // the pool still serves, and still checks
        Assert.True(next.ContainsEdge(0, 1));
        Assert.Throws<DagCycleException<int>>(() => next.AddEdge(1, 0));
    }

    [Fact]
    public void SmallSearches_AfterALargeOne_ShrinkTheSearchSet()
    {
        const int n = 4_000;                                          // within the pool's bound: kept as is
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) =>
        {
            for (var i = 0; i < k; i++) s.AddNode(i, i);
            for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1);    // the cycle check visits every node
        });
        dag = dag.Mutate(static s => { s.AddNode(-1, -1); s.AddNode(-2, -2); s.AddEdge(-1, -2); });
        var large = ImmDag<int, int, NoPayload>.Transient.Pooled!.SearchCapacity;
        Assert.True(large >= n, $"search set {large}");
        for (var i = 0; i < 16; i++) Assert.True(dag.TryAddEdge(-1, -2, out _, out _));   // present: no search
        Assert.Equal(large, ImmDag<int, int, NoPayload>.Transient.Pooled!.SearchCapacity);
        for (var i = 0; i < 16; i++) dag.AddEdge(n - 2, -1);         // a search of two nodes, sixteen times
        Assert.True(ImmDag<int, int, NoPayload>.Transient.Pooled!.SearchCapacity < 256, "the search set did not shrink");
        var bytes = Alloc.SteadyState(() => dag.TryAddEdge(n - 2, -1, out _, out _));
        long fresh = 0;                                               // the same edit on a thread with no history
        var t = new Thread(() => fresh = Alloc.SteadyState(() => dag.TryAddEdge(n - 2, -1, out _, out _)));
        t.Start();
        t.Join();
        Assert.Equal(fresh, bytes);                                   // the version's path copies only: no scratch
    }
}

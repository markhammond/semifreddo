using Semifreddo.Internal;
using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>The checkers must reject violations, not just accept valid structures.</summary>
public class CheckerSelfTests
{
    private static ImmDag<int, int, NoPayload> Chain() =>
        ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 1; i <= 4; i++) s.AddNode(i, i); s.AddEdge(1, 2); s.AddEdge(2, 3); s.AddEdge(3, 4); }, new DagBatchOptions { SkipCycleCheck = true });

    [Fact]
    public void Cycle_RejectsFakeCycles()
    {
        var g = Chain();
        Assert.Throws<InvariantViolationException>(() => Check.Cycle<int, ImmDag<int, int, NoPayload>>(g, [1, 2, 1]));
        Assert.Throws<InvariantViolationException>(() => Check.Cycle<int, ImmDag<int, int, NoPayload>>(g, [1, 2]));
    }

    [Fact]
    public void TopologicalOrder_RejectsBackwardEdges()
    {
        var g = Chain();
        var wrong = IntOrderOnly.Empty.Add(4).Add(3).Add(2).Add(1);
        Assert.Throws<InvariantViolationException>(() => Check.TopologicalOrder(g, new[] { 1, 2, 3, 4 }, wrong));
        Assert.True(wrong.TryAddEdges(g, [(1, 2), (2, 3), (3, 4)], out var fixedOrder));
        Check.TopologicalOrder(g, new[] { 1, 2, 3, 4 }, fixedOrder);
        Assert.Throws<InvariantViolationException>(() => Check.TopologicalOrder(g, new[] { 1, 2, 3, 4, 5 }, fixedOrder)); // missing node
    }

    [Fact]
    public void Levels_ExactRejectsLooseLevels()
    {
        var g = Chain();
        var o = IntTopoOrder.Empty.Add(1).Add(2).Add(3).Add(4);
        Assert.Throws<InvariantViolationException>(() => Check.Levels(g, new[] { 1, 2, 3, 4 }, o)); // all level 0
        Assert.True(o.TryAddEdges(g, [(1, 2), (2, 3), (3, 4)], out o));
        Check.Levels(g, new[] { 1, 2, 3, 4 }, o, exact: true);
        var cut = g.RemoveEdge(2, 3);
        Check.Levels(cut, new[] { 1, 2, 3, 4 }, o);                                                   // loose but valid
        Assert.Throws<InvariantViolationException>(() => Check.Levels(cut, new[] { 1, 2, 3, 4 }, o, exact: true));
    }

    [Fact]
    public void FlexVector_DetectsCorruptedSizeTable()
    {
        var v = ImmFlexVector<int>.Empty;
        for (var k = 0; k < 2000;) { var m = 1 + k % 37; v += ImmFlexVector.Create<int>(Enumerable.Range(k, m).ToArray()); k += m; }
        Check.FlexVector(v);
        static FRelaxed<int>? FindRelaxed(FNode<int> n, int shift)
        {
            if (shift == 0) return null;
            var b = FTree<int>.AsBranch(n);
            if (b is FRelaxed<int> r) return r;
            for (var j = 0; j < b.Len; j++) if (FindRelaxed(b.C[j]!, shift - 5) is { } x) return x;
            return null;
        }
        var relaxed = FindRelaxed(v.Head!.Root!, v.Head.Shift);
        Assert.NotNull(relaxed);
        relaxed!.Sz[0]++; // corrupt (test-only mutation of a private copy)
        Assert.Throws<InvariantViolationException>(() => Check.FlexVector(v));
    }

    [Fact]
    public void Dag_And_Champ_AcceptValidStructures()
    {
        var hub = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 100; i++) s.AddNode(i, -i); for (var i = 1; i < 100; i++) s.AddEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true });
        Check.Dag(hub);
        Check.Map(ImmMap.CreateRange(Enumerable.Range(0, 5000).Select(i => KeyValuePair.Create(i, i))));
        Check.Set(ImmSet.Create<int>(Enumerable.Range(0, 5000).ToArray()));
    }
}

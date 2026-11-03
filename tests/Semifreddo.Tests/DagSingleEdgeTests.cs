using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// A single persistent edge is proved acyclic before it is added (a search, or the order), so a refusal edits nothing:
/// <c>TryAddEdge</c> with and without an order, against a batch of one edge (<c>TryMutate</c>), which checks after.
/// </summary>
public class DagSingleEdgeTests
{
    private static ImmDag<int, int, NoPayload> Chain(int n)
        => ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) => { for (var i = 0; i < k; i++) s.AddNode(i, i); for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1); });

    private static ImmPkTopoOrder<int, MinValueSentinel<int>> OrderOf(int n)
    {
        var o = ImmPkTopoOrder.CreateWithMinValueSentinel<int>();
        for (var i = 0; i < n; i++) o = o.Add(i);
        return o;
    }

    /// <summary>A closed walk (first == last) over the graph's edges and from → to, which it uses.</summary>
    private static void AssertCycle(ImmDag<int, int, NoPayload> g, int from, int to, int[] cycle)
    {
        Assert.Equal(cycle[0], cycle[^1]);
        var usesNew = false;
        for (var i = 0; i + 1 < cycle.Length; i++)
        {
            if (cycle[i] == from && cycle[i + 1] == to) { usesNew = true; continue; }
            Assert.True(g.ContainsEdge(cycle[i], cycle[i + 1]), $"{cycle[i]} -> {cycle[i + 1]}");
        }
        Assert.True(usesNew, "the cycle does not use the new edge");
    }

    [Fact]
    public void TryAddEdge_AcceptsRefusesAndKeepsPresentEdges()
    {
        var g = Chain(5);
        Assert.True(g.TryAddEdge(0, 4, out var a, out var none));
        Assert.Null(none);
        Assert.True(a.ContainsEdge(0, 4));
        Assert.Equal(g.EdgeCount + 1, a.EdgeCount);
        Check.Dag(a);

        Assert.True(g.TryAddEdge(1, 2, out var same, out _));      // present
        Assert.True(same.IsSameAs(g));

        Assert.False(g.TryAddEdge(4, 1, out var refused, out var cycle));
        Assert.True(refused.IsSameAs(g));
        Assert.Equal([4, 1, 2, 3, 4], cycle);
        AssertCycle(g, 4, 1, cycle);

        Assert.False(g.TryAddEdge(2, 2, out refused, out cycle));  // self-loop
        Assert.True(refused.IsSameAs(g));
        Assert.Equal([2, 2], cycle);

        Assert.Throws<KeyNotFoundException>(() => g.TryAddEdge(0, 9, out _, out _));
        Assert.Throws<KeyNotFoundException>(() => g.TryAddEdge(9, 0, out _, out _));
        var e = Assert.Throws<DagCycleException<int>>(() => g.AddEdge(4, 0));
        AssertCycle(g, 4, 0, e.Cycle);
        Assert.Equal($"Batch introduces a cycle: {string.Join(" -> ", e.Cycle)}", e.Message);   // built when read
    }

    [Fact]
    public void TryAddEdge_WithAnOrder_LeavesBothAloneOnARefusal()
    {
        var g = Chain(5);
        var order = OrderOf(5);
        var before = order;
        Assert.False(g.TryAddEdge(ref order, 4, 1, out var refused, out var cycle));
        Assert.True(refused.IsSameAs(g));
        Assert.True(order.IsSameAs(before));
        AssertCycle(g, 4, 1, cycle);

        Assert.False(g.TryAddEdge(ref order, 3, 3, out _, out cycle));
        Assert.Equal([3, 3], cycle);
        Assert.True(order.IsSameAs(before));

        Assert.True(g.TryAddEdge(ref order, 1, 2, out var same, out _));      // present
        Assert.True(same.IsSameAs(g));
        Assert.True(order.IsSameAs(before));

        var h = g.AddNode(ref order, 9, 9, default);
        Assert.True(h.TryAddEdge(ref order, 9, 0, out var added, out _));      // disagrees with the order: moved
        Assert.True(order.Precedes(9, 0));
        Check.Dag(added);
        Assert.Throws<ArgumentException>(() => g.TryAddEdge(ref order, 0, 1, out _, out _));   // the order holds 9 too
    }

    [Fact]
    public void ARefusal_AllocatesOnlyItsCycle()
    {
        var g = Chain(40);
        var order = OrderOf(40);
        var bytes = Alloc.SteadyState(() => g.TryAddEdge(39, 0, out _, out _));
        Assert.True(bytes <= 24 + 4 * 42 + 8, $"refusal allocated {bytes} B");   // int[42]: the cycle 39, 0, …, 39
        var ordered = Alloc.SteadyState(() => { var o = order; g.TryAddEdge(ref o, 39, 0, out _, out _); });
        Assert.True(ordered <= 24 + 4 * 42 + 8, $"ordered refusal allocated {ordered} B");   // the order's cycle 0, …, 39, 0
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomEdges_AgreeWithABatchOfOne(int seed)
    {
        var rnd = new Random(seed);
        const int n = 60;
        var g = ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) => { for (var i = 0; i < k; i++) s.AddNode(i, i); });
        var withOrder = g;
        var order = OrderOf(n);
        for (var step = 0; step < 3_000; step++)
        {
            int from = rnd.Next(n), to = rnd.Next(n);
            var viaBatch = g.TryMutate((from, to), static (s, e) => s.AddEdge(e.from, e.to), out var expected, out _);
            var direct = g.TryAddEdge(from, to, out var actual, out var cycle);
            Assert.Equal(viaBatch, direct);
            if (direct) Assert.Equal(expected.EdgeCount, actual.EdgeCount);
            else
            {
                Assert.True(actual.IsSameAs(g));
                AssertCycle(g, from, to, cycle!);
            }

            var orderBefore = order;
            var ordered = withOrder.TryAddEdge(ref order, from, to, out var next, out var orderedCycle);
            Assert.Equal(direct, ordered);
            if (ordered) withOrder = next;
            else
            {
                Assert.True(order.IsSameAs(orderBefore));
                AssertCycle(withOrder, from, to, orderedCycle!);
            }

            if (direct) g = actual;
            if (rnd.Next(10) == 0 && g.EdgeCount > 0)                      // sometimes take an edge away again
            {
                var victim = g.Successors(from).Count > 0 ? (from, g.Successors(from).First()) : (-1, -1);
                if (victim.Item1 >= 0)
                {
                    g = g.RemoveEdge(victim.Item1, victim.Item2);
                    withOrder = withOrder.RemoveEdge(victim.Item1, victim.Item2);
                }
            }
            if (step % 500 == 0)
            {
                Check.Dag(g);
                Check.Dag(withOrder);
                Assert.Equal(g.EdgeCount, withOrder.EdgeCount);
                foreach (var v in withOrder)
                    foreach (var w in v.Successors) Assert.True(order.Precedes(v.Id, w));
            }
        }
    }
}

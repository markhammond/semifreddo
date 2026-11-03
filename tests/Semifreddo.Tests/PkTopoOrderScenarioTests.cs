using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// Scenario tests carried over from the previous implementation's DagTests: build chains and joins edge by edge,
/// reject a cycle, re-link (forcing a reorder), rename a node; after each step the order must be a topological order
/// of the graph. Here a cycle-closing edge is rejected (and reported) instead of added then removed.
/// </summary>
public class PkTopoOrderScenarioTests
{
    private ImmDag<int, int, NoPayload> _dag = ImmDag<int, int, NoPayload>.Empty;
    private IntOrderOnly _order = IntOrderOnly.Empty;

    private PkTopoOrderScenarioTests Vertex(int v)
    {
        if (_dag.ContainsNode(v)) return this;
        _dag = _dag.AddNode(v, v);
        _order = _order.Add(v);
        return this;
    }

    private bool Link(int from, int to)
    {
        var with = _dag.Mutate((from, to), static (s, e) => s.AddEdge(e.from, e.to), new DagBatchOptions { SkipCycleCheck = true });
        if (!_order.TryAddEdge(with, from, to, out var next, out var cycle))
        {
            Assert.Equal(cycle[0], cycle[^1]);                                  // the cycle is reported
            return false;
        }
        _dag = with;
        _order = next;
        return true;
    }

    private void Unlink(int from, int to) => _dag = _dag.RemoveEdge(from, to);   // removing an edge never invalidates the order

    private int[] Order()
    {
        var nodes = new List<int>();
        foreach (var n in _order) nodes.Add(n);
        return nodes.ToArray();
    }

    private void AssertTopological()
    {
        var visited = new HashSet<int>();
        foreach (var v in Order())
        {
            visited.Add(v);
            foreach (var dependency in _dag.Predecessors(v))
                Assert.True(visited.Contains(dependency), $"{dependency} should come before {v}");
        }
        Assert.Equal(_dag.NodeCount, visited.Count);
    }

    private void BuildChainOfSix()
    {
        Vertex(1).Vertex(2);
        Assert.True(Link(1, 2));
        Assert.Equal([1, 2], Order());
        Vertex(3);
        Assert.True(Link(2, 3));
        Assert.Equal([1, 2, 3], Order());
        Vertex(4);
        Assert.True(Link(3, 4));
        Assert.Equal([1, 2, 3, 4], Order());
        Vertex(5).Vertex(6);
        Assert.True(Link(5, 6));
        Assert.True(Link(4, 5));
        Assert.Equal([1, 2, 3, 4, 5, 6], Order());
    }

    [Fact]
    public void ChainsJoins_CycleRejection_AndReordering()
    {
        BuildChainOfSix();
        var before = _order;
        Assert.False(Link(6, 1));                                               // would close 1 -> ... -> 6 -> 1
        Assert.True(before.IsSameAs(_order));
        Assert.False(_dag.ContainsEdge(6, 1));
        Assert.Equal([1, 2, 3, 4, 5, 6], Order());

        Unlink(3, 4);
        Assert.True(Link(4, 1));                                                // 4 -> 1 -> 2 -> 3, 4 -> 5 -> 6: 4 moves first
        AssertTopological();
        Assert.Equal(4, Order()[0]);
    }

    [Fact]
    public void MoreEdges_AndRenaming()
    {
        BuildChainOfSix();
        Unlink(3, 4);
        Assert.True(Link(4, 1));
        Assert.True(Link(4, 2));
        AssertTopological();

        _dag = _dag.ReplaceNode(6, 7, 7, default);                             // rename 6 -> 7, edges kept
        _order = _order.Replace(6, 7);                                          // 7 takes 6's position
        var renamed = Order();
        Assert.DoesNotContain(6, renamed);
        Assert.Contains(7, renamed);
        Assert.True(_dag.ContainsEdge(5, 7));
        AssertTopological();
    }

    [Fact]
    public void EdgeCases_EmptySingleAndSelfLoop()
    {
        Assert.Empty(Order());
        Vertex(42);
        Assert.Equal([42], Order());
        Assert.ThrowsAny<Exception>(() => _dag.AddEdge(42, 42));                // a self-loop is a cycle: rejected
        Assert.False(_dag.ContainsEdge(42, 42));
        Assert.Equal([42], Order());
    }
}

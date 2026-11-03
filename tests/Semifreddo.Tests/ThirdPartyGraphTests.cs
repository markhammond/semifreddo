using System.Runtime.InteropServices;
using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>Topological orders over a graph that is not an ImmDag: only the public IDagAdjacency / Adjacency surface.</summary>
public class ThirdPartyGraphTests
{
    private sealed class ListGraph(int n) : IDagAdjacency<int>
    {
        private readonly List<int>[] _succ = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        private readonly List<int>[] _pred = Enumerable.Range(0, n).Select(_ => new List<int>()).ToArray();
        public void Link(int a, int b) { _succ[a].Add(b); _pred[b].Add(a); }
        public void Unlink(int a, int b) { _succ[a].Remove(b); _pred[b].Remove(a); }
        public Adjacency<int> Successors(int node) => new(CollectionsMarshal.AsSpan(_succ[node]));  // contiguous: allocation-free
        public Adjacency<int> Predecessors(int node) => new(_pred[node]);                           // collection fallback
        public IEnumerable<(int, int)> Edges => _succ.SelectMany((s, a) => s.Select(b => (a, b)));
    }

    [Fact]
    public void BothVariants_AgreeWithOracle_OnACustomGraph()
    {
        const int n = 120;
        var rnd = new Random(3);
        var g = new ListGraph(n);
        var lv = IntTopoOrder.Empty;
        var plain = IntOrderOnly.Empty;
        var oracle = KahnTopoOrder<int>.Empty;
        for (var i = 0; i < n; i++) { lv = lv.Add(i); plain = plain.Add(i); oracle = oracle.Add(i); }
        for (var k = 0; k < 600; k++)
        {
            int a = rnd.Next(n), b = rnd.Next(n);
            if (a == b || g.Edges.Contains((a, b))) continue;
            g.Link(a, b);
            var okO = oracle.TryAddEdge(g, a, b, out var no);
            Assert.Equal(okO, lv.TryAddEdge(g, a, b, out var nl));
            Assert.Equal(okO, plain.TryAddEdge(g, a, b, out var np));
            if (okO) { oracle = no; lv = nl; plain = np; } else g.Unlink(a, b);
        }
        foreach (var (a, b) in g.Edges)
        {
            Assert.True(lv.Precedes(a, b) && plain.Precedes(a, b));
            Assert.True(lv.Level(a) < lv.Level(b));
        }
    }
}

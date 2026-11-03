using System.Numerics;
using Semifreddo.Internal;
using Xunit;

namespace Semifreddo.Tests;

public class DagFeatureTests
{
    private static ImmDag<int, int, NoPayload> Chain(int n) =>
        ImmDag<int, int, NoPayload>.Empty.Mutate(n, static (s, k) => { for (var i = 0; i < k; i++) s.AddNode(i, -i); for (var i = 0; i + 1 < k; i++) s.AddEdge(i, i + 1); }, new DagBatchOptions { SkipCycleCheck = true });

    // ------------------------------------------------------------------ fixes

    [Fact]
    public void TryMutate_SelfLoop_ReturnsFalseWithCycle()
    {
        var d = Chain(3);
        Assert.False(d.TryMutate(0, static (s, _) => s.AddEdge(1, 1), out var r, out var cycle));
        Assert.True(r.IsSameAs(d));
        Assert.Equal([1, 1], cycle);
    }

    [Fact]
    public void DuplicateEdge_NoOpBatch_IsIdentical()
    {
        var d = Chain(5);
        Assert.True(d.Mutate(static s => Assert.False(s.AddEdge(1, 2))).IsSameAs(d));
        Assert.True(d.AddEdge(1, 2).IsSameAs(d));
    }

    [Fact]
    public void AddEdge_MissingTarget_LeavesNoPhantomNode()
    {
        var d = Chain(3);
        var t = d.ToTransient();
        Assert.Throws<KeyNotFoundException>(() => t.AddEdge(0, 99));
        Assert.Equal(3, t.NodeCount);
        Assert.False(t.ContainsNode(99));
        Assert.True(t.ToPersistent().IsSameAs(d) || t.ToPersistent().NodeCount == 3);
    }

    [Fact]
    public void EdgeSetEnumerator_Reset_Restarts()
    {
        var hub = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 40; i++) s.AddNode(i, i); for (var i = 1; i < 40; i++) s.AddEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true });
        foreach (var set in new[] { hub.Successors(0), hub.Predecessors(5) }) // CHAMP-backed and inline
        {
            var e = set.GetEnumerator();
            var first = new List<int>(); while (e.MoveNext()) first.Add(e.Current);
            e.Reset();
            var second = new List<int>(); while (e.MoveNext()) second.Add(e.Current);
            Assert.Equal(first, second);
            Assert.Equal(set.Count, first.Count);
        }
    }

    // ------------------------------------------------------------------ net change log

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ChangeLog_IsNetDiffAgainstBatchStart(int seed)
    {
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty;
        var nodes = new HashSet<int>();
        var edges = new HashSet<(int, int)>();
        var nextId = 0;
        for (var batch = 0; batch < 200; batch++)
        {
            var startNodes = nodes.ToHashSet();
            var startEdges = edges.ToHashSet();
            var ops = Enumerable.Range(0, 1 + rnd.Next(30)).Select(_ => (rnd.Next(100), rnd.Next(), rnd.Next())).ToArray();
            var state = (nodes, edges, ops, startNodes, startEdges, next: nextId);
            dag = dag.Mutate(state, static (s, st) =>
            {
                var next = st.next;
                foreach (var (r, x, y) in st.ops)
                {
                    var ids = st.nodes.ToArray();
                    if (r < 25 || ids.Length < 2) { var id = next++; Assert.True(s.AddNode(id, id)); st.nodes.Add(id); continue; }
                    int a = ids[x % ids.Length], b = ids[y % ids.Length];
                    if (r < 60 && a < b) { if (s.AddEdge(a, b)) st.edges.Add((a, b)); }            // ids increase along edges: acyclic
                    else if (r < 75) { if (s.RemoveEdge(a, b)) st.edges.Remove((a, b)); }
                    else if (r < 88) { Assert.True(s.RemoveNode(a)); st.nodes.Remove(a); st.edges.RemoveWhere(e => e.Item1 == a || e.Item2 == a); }
                    else if (r < 94)
                    {
                        // rename to a fresh, larger id keeps "ids increase along edges" only for successors; use it on sinks
                        if (!s.Successors(a).IsEmpty) continue;
                        var id = next++;
                        Assert.True(s.ReplaceNode(a, id, id, default));
                        st.nodes.Remove(a); st.nodes.Add(id);
                        var moved = st.edges.Where(e => e.Item2 == a).ToArray();
                        foreach (var e in moved) { st.edges.Remove(e); st.edges.Add((e.Item1, id)); }
                    }
                    else { if (s.ContainsEdge(a, b) && s.RemoveEdge(a, b)) { Assert.True(s.AddEdge(a, b)); } } // remove + re-add: net no-op
                }
                Assert.True(st.startEdges.Union(st.edges).Except(st.startEdges).ToHashSet().SetEquals(s.AddedEdges.ToArray()));
                Assert.True(st.startEdges.Except(st.edges).ToHashSet().SetEquals(s.RemovedEdges.ToArray()));
                Assert.True(st.nodes.Except(st.startNodes).ToHashSet().SetEquals(s.AddedNodes.ToArray()));
                Assert.True(st.startNodes.Except(st.nodes).ToHashSet().SetEquals(s.RemovedNodes.ToArray()));
                Assert.Equal(s.AddedEdges.Length, s.AddedEdges.ToArray().Distinct().Count());
            }, new DagBatchOptions { SkipCycleCheck = true });
            nextId += ops.Count(o => o.Item1 < 25 || o.Item1 is >= 88 and < 94) + 200; // stay ahead of ids issued in the batch
            Assert.Equal(nodes.Count, dag.NodeCount);
            Assert.Equal(edges.Count, dag.EdgeCount);
        }
    }

    // ------------------------------------------------------------------ payload, rename

    private sealed class Payload(string name) { public string Name = name; }

    [Fact]
    public void Payload_RoundTrips_AndVersionsAreIndependent()
    {
        var a = new Payload("a");
        var d1 = ImmDag<int, string, Payload>.Empty.AddNode(1, "one", a).AddNode(2, "two", new Payload("b")).AddEdge(1, 2);
        Assert.True(d1.TryGetValue(1, out var v) && ReferenceEquals(v, a));
        var d2 = d1.SetValue(1, new Payload("a2"));
        Assert.Same(a, d1.TryGetValue(1, out var old) ? old : null);
        Assert.Equal("a2", d2.TryGetValue(1, out var nv) ? nv.Name : null);
        Assert.True(d2.ContainsEdge(1, 2));
        Assert.Contains(d2, n => n.Id == 2 && n.Value.Name == "b" && n.Predecessors.Contains(1));
    }

    [Fact]
    public void ReplaceNode_KeepsEdges_AndUpdatesIndexes()
    {
        var d = ImmDag<int, string, NoPayload>.Empty.Mutate(static s =>
        {
            for (var i = 0; i < 5; i++) s.AddNode(i, "k" + i);
            s.AddEdge(0, 2); s.AddEdge(1, 2); s.AddEdge(2, 3); s.AddEdge(2, 4);
        }, new DagBatchOptions { SkipCycleCheck = true });
        var r = d.ReplaceNode(2, 20, "k20", default);
        Assert.False(r.ContainsNode(2));
        Assert.Equal([0, 1], r.Predecessors(20).Order().ToArray());
        Assert.Equal([3, 4], r.Successors(20).Order().ToArray());
        Assert.True(r.Successors(0).Contains(20) && !r.Successors(0).Contains(2));
        Assert.True(r.Predecessors(3).Contains(20));
        Assert.True(r.TryGetNodeByAlternateKey("k20", out var id) && id == 20);
        Assert.False(r.TryGetNodeByAlternateKey("k2", out _));
        Assert.Equal(d.EdgeCount, r.EdgeCount);
        Assert.True(r.ReplaceNode(20, 21).TryGetAlternateKey(21, out var keep) && keep == "k20"); // keep key & value
        Assert.Throws<ArgumentException>(() => r.ReplaceNode(20, 3));                              // id taken
        Assert.Throws<ArgumentException>(() => r.ReplaceNode(20, 30, "k3", default));             // alternate key taken

        // the topological order follows a rename with Replace and stays valid
        var order = IntTopoOrder.Empty.Mutate(d, static (t, g) => { foreach (var n in g) t.Add(n.Id); });
        var dd = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 5; i++) s.AddNode(i, i); s.AddEdge(0, 2); s.AddEdge(2, 3); }, new DagBatchOptions { SkipCycleCheck = true });
        Assert.True(order.TryAddEdges(dd, [(0, 2), (2, 3)], out order));
        var renamed = dd.ReplaceNode(2, 20);
        order = order.Replace(2, 20);
        Assert.True(order.Precedes(0, 20) && order.Precedes(20, 3));
    }

    [Fact]
    public void Origin_And_ReplacedNodes_DescribeTheBatch()
    {
        var d = ImmDag<int, string, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i < 4; i++) s.AddNode(i, "k" + i); s.AddEdge(0, 1); }, new DagBatchOptions { SkipCycleCheck = true });
        var t = d.ToTransient();
        Assert.True(t.Origin.IsSameAs(d));
        t.ReplaceNode(1, 10);
        t.ReplaceNode(10, 11);      // chain collapses
        t.ReplaceNode(2, 20);
        t.ReplaceNode(20, 2);       // renamed back: cancels
        t.AddNode(7, "k7");
        t.ReplaceNode(7, 70);       // created this batch: not a rename of anything at the start
        t.RemoveNode(3);
        Assert.Equal([(1, 11)], t.ReplacedNodes.ToArray());
        Assert.True(t.Origin.TryGetNode(3, out var three) && three.AlternateKey == "k3"); // removed node still readable at the origin
        Assert.Contains(3, t.RemovedNodes.ToArray());
        var next = t.ToPersistent();
        Assert.True(t.Origin.IsSameAs(next));  // freezing starts a new batch
        Assert.True(t.ReplacedNodes.IsEmpty);
        t.ReplaceNode(11, 12);
        t.RemoveNode(12);                       // rename then remove: no longer a rename
        Assert.True(t.ReplacedNodes.IsEmpty);
        Assert.Contains(11, t.RemovedNodes.ToArray());
    }

    // ------------------------------------------------------------------ ownership-aware edge sets

    private static void AssertExact<TE>(ChampNode<TE> n, int shift)
    {
        Assert.False(n.Slack);
        if (shift < 35)
        {
            Assert.Equal(BitOperations.PopCount(n.DataMap), n.Data.Length);
            Assert.Equal(BitOperations.PopCount(n.NodeMap), n.Nodes.Length);
        }
        foreach (var c in n.Nodes) AssertExact(c, shift + 5);
    }

    [Fact]
    public void HubEdgeSets_EditInPlace_FreezeExact_AndKeepHistory()
    {
        const int fan = 5000;
        var d0 = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i <= fan * 2; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
        var d1 = d0.Mutate(static s => { for (var i = 1; i <= fan; i++) s.AddEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true });
        var root = Assert.IsType<ChampNode<SetEntry<int>>>(d1.Successors(0).Raw);
        AssertExact(root, 0);                                     // spare capacity trimmed at freeze
        Assert.Equal(fan, d1.Successors(0).Count);

        var d2 = d1.Mutate(static s => { for (var i = fan + 1; i <= fan * 2; i++) s.AddEdge(0, i); for (var i = 1; i <= fan / 2; i++) s.RemoveEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true });
        Assert.Equal(fan, d1.Successors(0).Count);               // the earlier version is untouched
        Assert.True(Enumerable.Range(1, fan).All(d1.Successors(0).Contains));
        Assert.Equal(fan * 2 - fan / 2, d2.Successors(0).Count);
        Assert.True(d2.Successors(0).ToHashSet().SetEquals(Enumerable.Range(fan / 2 + 1, fan * 2 - fan / 2)));

        var bytes = Alloc.SteadyState(() => d0.Mutate(static s => { for (var i = 1; i <= fan; i++) s.AddEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true }), warmup: 2, trials: 3);
        Assert.True(bytes / fan < 200, $"{bytes / fan} B per hub edge"); // persistent set adds cost ~200+ B/edge
    }
}

using System.Numerics;
using Semifreddo.Internal;

namespace Semifreddo.Testing;

/// <summary>Thrown by <see cref="Check"/> and <see cref="DifferentialHarness"/> when an invariant does not hold.</summary>
public sealed class InvariantViolationException(string message) : Exception(message);

/// <summary>
/// Invariant checkers for Semifreddo's structures and for any <see cref="ITopologicalOrder{TSelf, N}"/>. Framework-agnostic:
/// each method returns normally or throws <see cref="InvariantViolationException"/> describing the first violation.
/// </summary>
public static class Check
{
    private static void Fail(string message) => throw new InvariantViolationException(message);

    // ------------------------------------------------------------------------------------- topological orders

    /// <summary>
    /// <paramref name="order"/> holds exactly <paramref name="graphNodes"/>, every edge of <paramref name="graph"/> goes
    /// forward in it, and enumeration is in rank order.
    /// </summary>
    public static void TopologicalOrder<N, TGraph, TOrder>(TGraph graph, IEnumerable<N> graphNodes, TOrder order)
        where N : notnull
        where TGraph : IDagAdjacency<N>, allows ref struct
        where TOrder : ITopologicalOrder<TOrder, N>
    {
        var count = 0;
        foreach (var n in graphNodes)
        {
            count++;
            if (!order.TryGetRank(n, out var ru)) Fail($"order is missing node {n}");
            foreach (var s in graph.Successors(n))
            {
                if (!order.TryGetRank(s, out var rv)) Fail($"order is missing node {s}");
                if (ru >= rv) Fail($"edge {n} -> {s} goes backwards in the order (rank {ru} >= {rv})");
            }
        }
        if (count != order.Count) Fail($"order has {order.Count} nodes, the graph {count}");
        var prev = long.MinValue;
        var seen = 0;
        foreach (var n in order)
        {
            order.TryGetRank(n, out var r);
            if (r <= prev) Fail($"enumeration is not in rank order at {n}");
            prev = r;
            seen++;
        }
        if (seen != order.Count) Fail($"enumeration yielded {seen} nodes, Count is {order.Count}");
    }

    /// <summary>
    /// Levels respect every edge (level(u) &lt; level(v)), per-level widths match, the last level is non-empty and, when
    /// <paramref name="exact"/>, every level equals the longest-path level (<see cref="ExactLevels"/>).
    /// </summary>
    public static void Levels<N, TOrder, TGraph>(TGraph graph, IEnumerable<N> graphNodes, TOrder order, bool exact = false)
        where N : notnull
        where TOrder : ILeveledTopologicalOrder<TOrder, N>
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var nodes = graphNodes as IReadOnlyCollection<N> ?? graphNodes.ToArray();
        var widths = new int[order.LevelCount];
        foreach (var n in nodes)
        {
            if (!order.TryGetLevel(n, out var lu)) Fail($"order has no level for {n}");
            if ((uint)lu >= (uint)widths.Length) Fail($"level {lu} of {n} is outside 0..{widths.Length - 1}");
            widths[lu]++;
            foreach (var s in graph.Successors(n))
            {
                order.TryGetLevel(s, out var lv);
                if (lu >= lv) Fail($"edge {n} -> {s} does not increase the level ({lu} >= {lv})");
            }
        }
        for (var l = 0; l < widths.Length; l++)
            if (widths[l] != order.LevelWidth(l)) Fail($"LevelWidth({l}) is {order.LevelWidth(l)}, {widths[l]} nodes have that level");
        if (widths.Length > 0 && widths[^1] == 0) Fail("the last level is empty");
        if (!exact) return;
        var expected = ExactLevels(graph, nodes);
        foreach (var n in nodes)
            if (!order.TryGetLevel(n, out var l) || l != expected[n]) Fail($"level of {n} is {l}, longest path gives {expected[n]}");
    }

    /// <summary>Longest-path levels by Kahn's algorithm (independent of the library's implementation).</summary>
    public static Dictionary<N, int> ExactLevels<N, TGraph>(TGraph graph, IEnumerable<N> graphNodes)
        where N : notnull
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var indegree = InSubsetIndegrees(graph, graphNodes);   // the graph restricted to graphNodes
        var level = indegree.Keys.ToDictionary(n => n, _ => 0);
        var ready = new Queue<N>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var done = 0;
        while (ready.TryDequeue(out var n))
        {
            done++;
            foreach (var s in graph.Successors(n))
            {
                if (!indegree.ContainsKey(s)) continue;          // outside the subset
                if (level[s] < level[n] + 1) level[s] = level[n] + 1;
                if (--indegree[s] == 0) ready.Enqueue(s);
            }
        }
        if (done != level.Count) Fail("graph has a cycle: levels are undefined");
        return level;
    }

    /// <summary>True if <paramref name="graph"/> restricted to <paramref name="graphNodes"/> has no cycle (Kahn).</summary>
    public static bool IsAcyclic<N, TGraph>(TGraph graph, IEnumerable<N> graphNodes)
        where N : notnull
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var indegree = InSubsetIndegrees(graph, graphNodes);
        var ready = new Queue<N>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var done = 0;
        while (ready.TryDequeue(out var n))
        {
            done++;
            foreach (var s in graph.Successors(n))
                if (indegree.ContainsKey(s) && --indegree[s] == 0) ready.Enqueue(s);
        }
        return done == indegree.Count;
    }

    /// <summary>Each node's in-degree counting only predecessors within <paramref name="graphNodes"/>.</summary>
    private static Dictionary<N, int> InSubsetIndegrees<N, TGraph>(TGraph graph, IEnumerable<N> graphNodes)
        where N : notnull
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var indegree = new Dictionary<N, int>();
        foreach (var n in graphNodes) indegree[n] = 0;
        foreach (var n in indegree.Keys.ToArray())
            foreach (var p in graph.Predecessors(n))
                if (indegree.ContainsKey(p)) indegree[n]++;
        return indegree;
    }

    /// <summary>A reported cycle is real: at least one edge, first == last, and each consecutive pair is an edge.</summary>
    public static void Cycle<N, TGraph>(TGraph graph, ReadOnlySpan<N> cycle)
        where N : notnull
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        if (cycle.Length < 2) Fail("a cycle needs at least one edge");
        if (!EqualityComparer<N>.Default.Equals(cycle[0], cycle[^1])) Fail($"cycle does not close: {cycle[0]} ... {cycle[^1]}");
        for (var i = 0; i + 1 < cycle.Length; i++)
        {
            var found = false;
            foreach (var s in graph.Successors(cycle[i]))
                if (EqualityComparer<N>.Default.Equals(s, cycle[i + 1])) { found = true; break; }
            if (!found) Fail($"cycle step {cycle[i]} -> {cycle[i + 1]} is not an edge");
        }
    }

    // ------------------------------------------------------------------------------------- ImmDag

    /// <summary>
    /// Successor and predecessor sets mirror each other, <see cref="ImmDag{N, A, V}.EdgeCount"/> matches, the
    /// alternate-key index is a bijection, and every edge set's storage is exact (inline or trimmed CHAMP).
    /// </summary>
    public static void Dag<N, A, V>(ImmDag<N, A, V> dag) where N : notnull where A : notnull
    {
        var edges = 0;
        foreach (var n in dag)
        {
            foreach (var s in n.Successors)
            {
                edges++;
                if (!dag.TryGetNode(s, out var sv)) Fail($"edge {n.Id} -> {s} targets a missing node");
                if (!sv.Predecessors.Contains(n.Id)) Fail($"edge {n.Id} -> {s} is missing from {s}'s predecessors");
            }
            foreach (var p in n.Predecessors)
                if (!dag.ContainsEdge(p, n.Id)) Fail($"predecessor {p} of {n.Id} has no matching edge");
            if (!dag.TryGetNodeByAlternateKey(n.AlternateKey, out var id) || !EqualityComparer<N>.Default.Equals(id, n.Id))
                Fail($"alternate key {n.AlternateKey} does not map back to {n.Id}");
            EdgeSetStorage(n.Successors);
            EdgeSetStorage(n.Predecessors);
        }
        if (edges != dag.EdgeCount) Fail($"EdgeCount is {dag.EdgeCount}, {edges} edges found");
        if (dag.Head is { } h && h.ByAlt.Count != dag.NodeCount) Fail($"alternate-key index has {h.ByAlt.Count} entries for {dag.NodeCount} nodes");
    }

    private static void EdgeSetStorage<N>(EdgeSet<N> set) where N : notnull
    {
        switch (set.Raw)
        {
            case null: break;
            case N[] a:
                if (a.Length is 0 or > EdgeSet<N>.MaxInline) Fail($"inline edge set of length {a.Length}");
                if (a.Distinct().Count() != a.Length) Fail("inline edge set has duplicates");
                break;
            case ChampNode<SetEntry<N>> root:
                if (root.Count <= EdgeSet<N>.MaxInline / 2) Fail($"CHAMP edge set of {root.Count} should be inline");
                Champ(root, 0, isRoot: true);
                break;
        }
    }

    // ------------------------------------------------------------------------------------- CHAMP maps and sets

    /// <summary>Frozen CHAMP nodes are exact-size (no spare capacity), canonical and their count matches.</summary>
    public static void Map<K, V>(ImmMap<K, V> map) where K : notnull
    {
        if (map.Root is null) return;
        var n = Champ(map.Root, 0, isRoot: true);
        if (n != map.Count) Fail($"Count is {map.Count}, {n} entries found");
    }

    /// <inheritdoc cref="Map{K, V}"/>
    public static void Set<T>(ImmSet<T> set) where T : notnull
    {
        if (set.Root is null) return;
        var n = Champ(set.Root, 0, isRoot: true);
        if (n != set.Count) Fail($"Count is {set.Count}, {n} entries found");
    }

    private static int Champ<TE>(ChampNode<TE> node, int shift, bool isRoot)
    {
        if (node.Slack) Fail("frozen CHAMP node still marked as having spare capacity");
        if (shift >= 35)
        {
            if (node.DataMap != 0 || node.NodeMap != 0 || node.Nodes.Length != 0) Fail("collision node with bitmaps or children");
            if (node.Data.Length < 2) Fail("collision node with fewer than two entries");
            return node.Data.Length;
        }
        int dc = BitOperations.PopCount(node.DataMap), nc = BitOperations.PopCount(node.NodeMap);
        if ((node.DataMap & node.NodeMap) != 0) Fail("a slot is both an entry and a child");
        if (node.Data.Length != dc) Fail($"data array length {node.Data.Length} != {dc} entries");
        if (node.Nodes.Length != nc) Fail($"child array length {node.Nodes.Length} != {nc} children");
        if (!isRoot && nc == 0 && dc == 1) Fail("non-canonical: a non-root node holds a single entry");
        var total = dc;
        foreach (var c in node.Nodes) total += Champ(c, shift + 5, isRoot: false);
        return total;
    }

    // ------------------------------------------------------------------------------------- RRB vector

    /// <summary>
    /// RRB structural rules: counts, tail bounds, dense nodes only where every child but the last is full, exact
    /// inline size tables padded with <c>int.MaxValue</c>, uniform leaf depth, and bounded height.
    /// </summary>
    public static void FlexVector<T>(ImmFlexVector<T> vector)
    {
        var h = vector.Head;
        if (h is null) return;
        if (h.Count <= 0) Fail("non-empty head with Count <= 0");
        if ((uint)h.TailLen > 32) Fail($"tail length {h.TailLen}");
        if ((h.TailLen == 0) != (h.Tail is null)) Fail($"tail {(h.Tail is null ? "missing" : "present")} at length {h.TailLen}");
        if (h.Tail is FTail<T> t && (t.Fill >= 0 ? t.Fill : ~t.Fill) < h.TailLen)
            Fail($"tail claimed or sealed at {(t.Fill >= 0 ? t.Fill : ~t.Fill)}, below its length {h.TailLen}");
        if (h.Tail is { } leaf and not FTail<T> && leaf.Count < h.TailLen) Fail($"tail leaf of {leaf.Count} below its length {h.TailLen}");
        if (h.Root is null)
        {
            if (h.Count != h.TailLen) Fail("tail-only vector whose Count != TailLen");
            return;
        }
        if (h.Shift < 5) Fail($"root shift {h.Shift}");
        if (h.TreeCount != h.Root.Count) Fail($"tree count {h.TreeCount} != root count {h.Root.Count}");
        FlexNode(h.Root, h.Shift);
        var optimal = 5;
        while ((1L << (optimal + 5)) < h.TreeCount) optimal += 5;
        if (h.Shift > optimal + 10) Fail($"height: shift {h.Shift} for {h.TreeCount} elements (optimal {optimal})");
    }

    private static void FlexNode<T>(FNode<T> n, int shift)
    {
        if (shift == 0)
        {
            if (n is not FLeaf<T>) Fail("non-leaf at leaf depth");
            if (n is FTail<T> t && t.Fill != ~t.Count) Fail($"tail in the tree not sealed at its count (fill {t.Fill}, count {t.Count})");
            if (n.Count is < 1 or > 32) Fail($"leaf with {n.Count} elements");
            return;
        }
        if (n is not FBranch<T> b) { Fail("leaf above leaf depth"); return; }
        if (b.Len is < 1 or > 32) Fail($"branch with {b.Len} children");
        var relaxed = b as FRelaxed<T>;
        var total = 0;
        for (var j = 0; j < b.Len; j++)
        {
            var c = b.C[j];
            if (c is null) { Fail($"null child {j}"); return; }
            FlexNode(c, shift - 5);
            total += c.Count;
            if (relaxed is null && j < b.Len - 1 && c.Count != 1 << shift) Fail($"dense branch with a non-full child {j}");
            if (relaxed is not null && relaxed.Sz[j] != total) Fail($"size table entry {j} is {relaxed.Sz[j]}, expected {total}");
        }
        for (var j = b.Len; j < 32; j++)
        {
            if (b.C[j] is not null) Fail($"stale child pointer in slot {j}");
            if (relaxed is not null && relaxed.Sz[j] != int.MaxValue) Fail($"size table padding at {j} is {relaxed.Sz[j]}");
        }
        if (total != b.Count) Fail($"branch count {b.Count} != sum of children {total}");
    }
}

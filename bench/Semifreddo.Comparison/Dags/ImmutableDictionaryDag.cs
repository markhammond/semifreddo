using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Semifreddo.Comparison.Dags;

/// <summary>Thrown when an edge would close a cycle. <see cref="Cycle"/> is one such cycle (first == last).</summary>
public sealed class CycleException<N>(N[] cycle) : InvalidOperationException("The edge would close a cycle.")
{
    public N[] Cycle { get; } = cycle;
}

/// <summary>
/// The persistent DAG one would assemble from System.Collections.Immutable, to measure <see cref="ImmDag{N, A, V}"/>
/// against: the same model (an id, a one-to-one alternate key and a payload per node, successor and predecessor sets)
/// as a map of nodes and a map of alternate keys. A new edge is checked by a depth-first search from its target for its
/// source; a batch edits the maps' builders and checks each edge as it is added. Written as its users would write it,
/// not as a port of ImmDag, except that the search reuses its scratch collections rather than allocating them per edge.
/// </summary>
public readonly struct ImmutableDictionaryDag<N, A, V> where N : notnull where A : notnull
{
    public readonly record struct Node(A AlternateKey, V Value, ImmutableHashSet<N> Successors, ImmutableHashSet<N> Predecessors);

    private readonly ImmutableDictionary<N, Node>? _nodes;
    private readonly ImmutableDictionary<A, N>? _byAlt;

    private ImmutableDictionaryDag(ImmutableDictionary<N, Node> nodes, ImmutableDictionary<A, N> byAlt, int edges)
    {
        _nodes = nodes;
        _byAlt = byAlt;
        EdgeCount = edges;
    }

    public static ImmutableDictionaryDag<N, A, V> Empty => default;

    private ImmutableDictionary<N, Node> Nodes => _nodes ?? ImmutableDictionary<N, Node>.Empty;
    private ImmutableDictionary<A, N> ByAlt => _byAlt ?? ImmutableDictionary<A, N>.Empty;

    public int NodeCount => Nodes.Count;
    public int EdgeCount { get; }
    public bool ContainsNode(N id) => Nodes.ContainsKey(id);
    public bool ContainsEdge(N from, N to) => Nodes.TryGetValue(from, out var n) && n.Successors.Contains(to);
    public bool TryGetNodeByAlternateKey(A alternateKey, [MaybeNullWhen(false)] out N id) => ByAlt.TryGetValue(alternateKey, out id);
    public ImmutableHashSet<N> Successors(N id) => Nodes[id].Successors;
    public ImmutableHashSet<N> Predecessors(N id) => Nodes[id].Predecessors;

    public ImmutableDictionaryDag<N, A, V> AddNode(N id, A alternateKey, V value)
    {
        if (Nodes.ContainsKey(id) || ByAlt.ContainsKey(alternateKey)) throw new ArgumentException($"Node {id} or alternate key {alternateKey} already present.");
        return new(Nodes.Add(id, new Node(alternateKey, value, [], [])), ByAlt.Add(alternateKey, id), EdgeCount);
    }

    /// <summary>Removes a node and its incident edges.</summary>
    public ImmutableDictionaryDag<N, A, V> RemoveNode(N id)
    {
        if (!Nodes.TryGetValue(id, out var node)) return this;
        var nodes = Nodes.Remove(id);
        foreach (var p in node.Predecessors)
        {
            var n = nodes[p];
            nodes = nodes.SetItem(p, n with { Successors = n.Successors.Remove(id) });
        }
        foreach (var s in node.Successors)
        {
            var n = nodes[s];
            nodes = nodes.SetItem(s, n with { Predecessors = n.Predecessors.Remove(id) });
        }
        return new(nodes, ByAlt.Remove(node.AlternateKey), EdgeCount - node.Predecessors.Count - node.Successors.Count);
    }

    public ImmutableDictionaryDag<N, A, V> AddEdge(N from, N to)
        => TryAddEdge(from, to, out var r, out var cycle) ? r : throw new CycleException<N>(cycle);

    /// <summary>Adds an edge unless it would close a cycle: then returns false, this, and the cycle.</summary>
    public bool TryAddEdge(N from, N to, out ImmutableDictionaryDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
    {
        result = this;
        var nodes = Nodes;
        var f = nodes[from];
        var t = nodes[to];
        if (f.Successors.Contains(to)) { cycle = null; return true; }
        if (TryFindCycle(nodes, from, to, out cycle)) return false;
        nodes = nodes.SetItem(from, f with { Successors = f.Successors.Add(to) })
                     .SetItem(to, t with { Predecessors = t.Predecessors.Add(from) });
        result = new(nodes, ByAlt, EdgeCount + 1);
        return true;
    }

    public ImmutableDictionaryDag<N, A, V> RemoveEdge(N from, N to)
    {
        var nodes = Nodes;
        var f = nodes[from];
        if (!f.Successors.Contains(to)) return this;
        var t = nodes[to];
        nodes = nodes.SetItem(from, f with { Successors = f.Successors.Remove(to) })
                     .SetItem(to, t with { Predecessors = t.Predecessors.Remove(from) });
        return new(nodes, ByAlt, EdgeCount - 1);
    }

    /// <summary>Kahn's algorithm: a topological order of the nodes, computed on demand.</summary>
    public N[] TopologicalOrder()
    {
        var nodes = Nodes;
        var order = new N[nodes.Count];
        var pending = new Dictionary<N, int>(nodes.Count);
        int head = 0, tail = 0;
        foreach (var (id, n) in nodes)
        {
            if (n.Predecessors.Count == 0) order[tail++] = id;
            else pending[id] = n.Predecessors.Count;
        }
        while (head < tail)
            foreach (var s in nodes[order[head++]].Successors)
                if (--CollectionsMarshal.GetValueRefOrNullRef(pending, s) == 0) order[tail++] = s;
        return order;
    }

    /// <summary>Applies a batch through the maps' builders; throws <see cref="CycleException{N}"/> on a cycle (this is
    /// unchanged).</summary>
    public ImmutableDictionaryDag<N, A, V> Mutate(Action<Builder> batch)
    {
        var b = new Builder(this);
        batch(b);
        return b.ToImmutable();
    }

    /// <summary>A batch's mutable view over the maps' builders: each edge is checked as it is added.</summary>
    public sealed class Builder
    {
        private readonly ImmutableDictionary<N, Node>.Builder _nodes;
        private readonly ImmutableDictionary<A, N>.Builder _byAlt;
        private int _edges;

        internal Builder(ImmutableDictionaryDag<N, A, V> dag)
        {
            _nodes = dag.Nodes.ToBuilder();
            _byAlt = dag.ByAlt.ToBuilder();
            _edges = dag.EdgeCount;
        }

        public void AddNode(N id, A alternateKey, V value)
        {
            if (_nodes.ContainsKey(id) || _byAlt.ContainsKey(alternateKey)) throw new ArgumentException($"Node {id} or alternate key {alternateKey} already present.");
            _nodes.Add(id, new Node(alternateKey, value, [], []));
            _byAlt.Add(alternateKey, id);
        }

        public void AddEdge(N from, N to)
        {
            var f = _nodes[from];
            var t = _nodes[to];
            if (f.Successors.Contains(to)) return;
            if (TryFindCycle(_nodes, from, to, out var cycle)) throw new CycleException<N>(cycle);
            _nodes[from] = f with { Successors = f.Successors.Add(to) };
            _nodes[to] = t with { Predecessors = t.Predecessors.Add(from) };
            _edges++;
        }

        internal ImmutableDictionaryDag<N, A, V> ToImmutable() => new(_nodes.ToImmutable(), _byAlt.ToImmutable(), _edges);
    }

    [ThreadStatic] private static Dictionary<N, N>? t_parent;
    [ThreadStatic] private static Stack<N>? t_stack;

    // from → to closes a cycle if to reaches from (or is from): depth-first from to, for from.
    private static bool TryFindCycle<TNodes>(TNodes nodes, N from, N to, [NotNullWhen(true)] out N[]? cycle)
        where TNodes : IReadOnlyDictionary<N, Node>
    {
        var parent = t_parent ??= [];
        var stack = t_stack ??= new();
        try
        {
            parent[to] = to;
            stack.Push(to);
            while (stack.TryPop(out var u))
            {
                if (EqualityComparer<N>.Default.Equals(u, from)) { cycle = DagCycles.FromParents(parent, from, to); return true; }
                foreach (var s in nodes[u].Successors)
                    if (parent.TryAdd(s, u)) stack.Push(s);
            }
            cycle = null;
            return false;
        }
        finally
        {
            parent.Clear();
            stack.Clear();
        }
    }
}

internal static class DagCycles
{
    /// <summary>The cycle from → to → … → from, read back from a search from <paramref name="to"/> that reached
    /// <paramref name="from"/> (<paramref name="parent"/>: each node's predecessor on the search, to's is itself).</summary>
    public static N[] FromParents<N>(Dictionary<N, N> parent, N from, N to) where N : notnull
    {
        var path = new List<N>();
        for (var n = from; ; n = parent[n])
        {
            path.Add(n);
            if (EqualityComparer<N>.Default.Equals(n, to)) break;
        }
        path.Reverse();             // to … from
        return [from, .. path];
    }
}

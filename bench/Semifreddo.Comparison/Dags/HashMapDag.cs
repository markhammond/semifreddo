using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using LanguageExt;

namespace Semifreddo.Comparison.Dags;

/// <summary>
/// The persistent DAG one would assemble from LanguageExt's collections (<c>HashMap</c>, <c>HashSet</c>), line for line
/// <see cref="ImmutableDictionaryDag{N, A, V}"/> with LanguageExt's maps and sets in place of System.Collections.Immutable's.
/// LanguageExt has no builders or transients, so it has no batch: a batch makes its edits one by one.
/// </summary>
public readonly struct HashMapDag<N, A, V> where N : notnull where A : notnull
{
    public readonly record struct Node(A AlternateKey, V Value, LanguageExt.HashSet<N> Successors, LanguageExt.HashSet<N> Predecessors);

    private readonly HashMap<N, Node> _nodes;
    private readonly HashMap<A, N> _byAlt;

    private HashMapDag(HashMap<N, Node> nodes, HashMap<A, N> byAlt, int edges)
    {
        _nodes = nodes;
        _byAlt = byAlt;
        EdgeCount = edges;
    }

    public static HashMapDag<N, A, V> Empty { get; } = new(HashMap<N, Node>.Empty, HashMap<A, N>.Empty, 0);

    public int NodeCount => _nodes.Count;
    public int EdgeCount { get; }
    public bool ContainsNode(N id) => _nodes.ContainsKey(id);

    public bool ContainsEdge(N from, N to)
    {
        var n = _nodes.Find(from);
        return n.IsSome && ((Node)n).Successors.Contains(to);
    }

    public bool TryGetNodeByAlternateKey(A alternateKey, [MaybeNullWhen(false)] out N id)
    {
        var n = _byAlt.Find(alternateKey);
        id = n.IsSome ? (N)n : default;
        return n.IsSome;
    }

    public LanguageExt.HashSet<N> Successors(N id) => _nodes[id].Successors;
    public LanguageExt.HashSet<N> Predecessors(N id) => _nodes[id].Predecessors;

    public HashMapDag<N, A, V> AddNode(N id, A alternateKey, V value)
    {
        if (_nodes.ContainsKey(id) || _byAlt.ContainsKey(alternateKey)) throw new ArgumentException($"Node {id} or alternate key {alternateKey} already present.");
        return new(_nodes.Add(id, new Node(alternateKey, value, LanguageExt.HashSet<N>.Empty, LanguageExt.HashSet<N>.Empty)),
            _byAlt.Add(alternateKey, id), EdgeCount);
    }

    /// <summary>Removes a node and its incident edges.</summary>
    public HashMapDag<N, A, V> RemoveNode(N id)
    {
        var found = _nodes.Find(id);
        if (found.IsNone) return this;
        var node = (Node)found;
        var nodes = _nodes.Remove(id);
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
        return new(nodes, _byAlt.Remove(node.AlternateKey), EdgeCount - node.Predecessors.Count - node.Successors.Count);
    }

    public HashMapDag<N, A, V> AddEdge(N from, N to)
        => TryAddEdge(from, to, out var r, out var cycle) ? r : throw new CycleException<N>(cycle);

    /// <summary>Adds an edge unless it would close a cycle: then returns false, this, and the cycle.</summary>
    public bool TryAddEdge(N from, N to, out HashMapDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
    {
        result = this;
        var nodes = _nodes;
        var f = nodes[from];
        var t = nodes[to];
        if (f.Successors.Contains(to)) { cycle = null; return true; }
        if (TryFindCycle(nodes, from, to, out cycle)) return false;
        nodes = nodes.SetItem(from, f with { Successors = f.Successors.Add(to) })
                     .SetItem(to, t with { Predecessors = t.Predecessors.Add(from) });
        result = new(nodes, _byAlt, EdgeCount + 1);
        return true;
    }

    public HashMapDag<N, A, V> RemoveEdge(N from, N to)
    {
        var nodes = _nodes;
        var f = nodes[from];
        if (!f.Successors.Contains(to)) return this;
        var t = nodes[to];
        nodes = nodes.SetItem(from, f with { Successors = f.Successors.Remove(to) })
                     .SetItem(to, t with { Predecessors = t.Predecessors.Remove(from) });
        return new(nodes, _byAlt, EdgeCount - 1);
    }

    /// <summary>Kahn's algorithm: a topological order of the nodes, computed on demand.</summary>
    public N[] TopologicalOrder()
    {
        var nodes = _nodes;
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

    [ThreadStatic] private static Dictionary<N, N>? t_parent;
    [ThreadStatic] private static Stack<N>? t_stack;

    // from → to closes a cycle if to reaches from (or is from): depth-first from to, for from.
    private static bool TryFindCycle(HashMap<N, Node> nodes, N from, N to, [NotNullWhen(true)] out N[]? cycle)
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

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Semifreddo.Testing;

/// <summary>
/// Reference <see cref="ITopologicalOrder{TSelf, N}"/> for differential testing: every edge operation recomputes a
/// canonical Kahn topological sort of the whole node set, ties broken by previous position (deterministic, and stable
/// when nothing needs to move). O(V + E) per call, deliberately simple, and independent of the library's own data
/// structures (System.Collections.Immutable only), so agreement with it is meaningful evidence.
/// </summary>
public sealed class KahnTopoOrder<N> : ILeveledTopologicalOrder<KahnTopoOrder<N>, N> where N : notnull
{
    private readonly ImmutableArray<N> _order;
    private readonly ImmutableDictionary<N, int> _position;
    private readonly ImmutableDictionary<N, int> _level;
    private readonly ImmutableArray<int> _widths;

    private KahnTopoOrder(ImmutableArray<N> order, ImmutableDictionary<N, int> level)
    {
        _order = order;
        var b = ImmutableDictionary.CreateBuilder<N, int>();
        for (var i = 0; i < order.Length; i++) b.Add(order[i], i);
        _position = b.ToImmutable();
        _level = level;
        var max = level.IsEmpty ? -1 : level.Values.Max();
        var w = new int[max + 1];
        foreach (var l in level.Values) w[l]++;
        _widths = [.. w];
    }

    public static KahnTopoOrder<N> Empty { get; } = new(ImmutableArray<N>.Empty, ImmutableDictionary<N, int>.Empty);

    public bool TryGetLevel(N node, out int level) => _level.TryGetValue(node, out level);
    public int LevelCount => _widths.Length;
    public int LevelWidth(int level) => (uint)level < (uint)_widths.Length ? _widths[level] : 0;

    /// <summary>Exact longest-path levels by brute force over the current order.</summary>
    public KahnTopoOrder<N> Tighten<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var level = new Dictionary<N, int>();
        foreach (var n in _order)
        {
            var l = 0;
            foreach (var p in graph.Predecessors(n)) if (level.TryGetValue(p, out var lp)) l = Math.Max(l, lp + 1);
            level[n] = l;
        }
        return new(_order, level.ToImmutableDictionary());
    }

    public int Count => _order.Length;
    public bool IsSameAs(KahnTopoOrder<N> other) => ReferenceEquals(this, other);
    public bool Contains(N node) => _position.ContainsKey(node);

    public bool TryGetRank(N node, out long rank)
    {
        var found = _position.TryGetValue(node, out var p);
        rank = p;
        return found;
    }

    public bool Precedes(N a, N b) => _position[a] < _position[b];

    public KahnTopoOrder<N> Add(N node) => Contains(node) ? this : new(_order.Add(node), _level.Add(node, 0));

    public KahnTopoOrder<N> Remove(N node) => Contains(node) ? new(_order.RemoveAt(_position[node]), _level.Remove(node)) : this;

    public KahnTopoOrder<N> Replace(N oldNode, N newNode)
        => Contains(oldNode) && !Contains(newNode)
            ? new(_order.SetItem(_position[oldNode], newNode), _level.Remove(oldNode).Add(newNode, _level[oldNode]))
            : this;

    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out KahnTopoOrder<N> result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        if (!Contains(from)) throw new KeyNotFoundException($"{from}");
        if (!Contains(to)) throw new KeyNotFoundException($"{to}");
        if (EqualityComparer<N>.Default.Equals(from, to)) { result = this; cycle = [from, to]; return false; }
        return Resort(graph, out result, out cycle);
    }

    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out KahnTopoOrder<N> result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        foreach (var (u, v) in edges)
            if (EqualityComparer<N>.Default.Equals(u, v)) { result = this; cycle = [u, v]; return false; }
        return Resort(graph, out result, out cycle);
    }

    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out KahnTopoOrder<N> result)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => TryAddEdge(graph, from, to, out result, out _);

    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out KahnTopoOrder<N> result)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => TryAddEdges(graph, edges, out result, out _);

    /// <summary>Kahn over the node set (edges to nodes outside the set are ignored), smallest previous position first.</summary>
    private bool Resort<TGraph>(TGraph graph, out KahnTopoOrder<N> result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var indegree = new Dictionary<N, int>();
        foreach (var n in _order)
        {
            var d = 0;
            foreach (var p in graph.Predecessors(n)) if (Contains(p)) d++;
            indegree[n] = d;
        }
        var ready = new PriorityQueue<N, int>();
        foreach (var (n, d) in indegree) if (d == 0) ready.Enqueue(n, _position[n]);

        var sorted = ImmutableArray.CreateBuilder<N>(_order.Length);
        while (ready.TryDequeue(out var n, out _))
        {
            sorted.Add(n);
            foreach (var s in graph.Successors(n))
                if (indegree.ContainsKey(s) && --indegree[s] == 0) ready.Enqueue(s, _position[s]);
        }

        if (sorted.Count == _order.Length)
        {
            var order = sorted.MoveToImmutable();
            result = new KahnTopoOrder<N>(order, ImmutableDictionary<N, int>.Empty).Tighten(graph); // exact levels
            cycle = null;
            return true;
        }

        // Every unsorted node keeps a predecessor that is also unsorted: walk predecessors until a node repeats.
        var unsorted = new HashSet<N>(_order.Where(n => indegree[n] > 0));
        var current = unsorted.First();
        var path = new List<N>();
        var at = new Dictionary<N, int>();
        while (!at.ContainsKey(current))
        {
            at[current] = path.Count;
            path.Add(current);
            foreach (var p in graph.Predecessors(current))
                if (unsorted.Contains(p)) { current = p; break; }
        }
        var loop = path.GetRange(at[current], path.Count - at[current]);
        loop.Reverse(); // walked against the edges
        loop.Add(loop[0]);
        result = this;
        cycle = [.. loop];
        return false;
    }

    public IEnumerator<N> GetEnumerator() => ((IEnumerable<N>)_order).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

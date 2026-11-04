using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Semifreddo;


/// <summary>
/// Persistent topological order of a DAG's nodes, maintained beside a graph it does not own. Values are immutable;
/// every operation returns the resulting order. Implementations: <see cref="ImmPkTopoOrder{N, TSentinel}"/>,
/// <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/> and, for differential testing, the Kahn oracle in Semifreddo.Testing. Program against this
/// through generic constraints (<c>where TOrder : ITopologicalOrder&lt;TOrder, N&gt;</c>) to avoid boxing.
/// </summary>
public interface ITopologicalOrder<TSelf, N> : IReadOnlyCollection<N>
    where TSelf : ITopologicalOrder<TSelf, N>
    where N : notnull
{
    static abstract TSelf Empty { get; }

    /// <summary>True if both are the same version (O(1); implies equal contents).</summary>
    bool IsSameAs(TSelf other);

    bool Contains(N node);

    /// <summary>Rank of <paramref name="node"/>; ranks are comparable within one version only.</summary>
    bool TryGetRank(N node, out long rank);

    bool Precedes(N a, N b);

    /// <summary>Adds a node with no constraints yet (no-op if present).</summary>
    TSelf Add(N node);

    /// <summary>Removes a node; never invalidates the order of the others.</summary>
    TSelf Remove(N node);

    /// <summary>Renames a node, keeping its position.</summary>
    TSelf Replace(N oldNode, N newNode);

    /// <summary>Restores the order after <paramref name="graph"/> gained from→to. False (and one cycle, first ==
    /// last) if that edge closes a cycle; the receiver is then returned unchanged.</summary>
    bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out TSelf result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct;

    /// <summary>Batch form of <see cref="TryAddEdge{TGraph}(TGraph, N, N, out TSelf, out N[])"/>: <paramref name="graph"/> already holds every edge.</summary>
    bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out TSelf result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct;

    /// <summary>Probe: as the cycle-reporting overload, but a failure only returns false (no cycle is built, so
    /// nothing is allocated). Diagnose later, e.g. on another thread, from the immutable inputs.</summary>
    bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out TSelf result)
        where TGraph : IDagAdjacency<N>, allows ref struct;

    /// <summary>Probe form of the batch overload (no cycle on failure).</summary>
    bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out TSelf result)
        where TGraph : IDagAdjacency<N>, allows ref struct;
}

/// <summary>
/// A topological order that also maintains levels (longest-path layers): nodes on the same level have no dependency
/// path between them, so each level is a parallel evaluation group. Levels are raised as edges are added; removals may
/// leave them higher than necessary (still valid) until <see cref="Tighten{TGraph}"/>. CRTP (<typeparamref name="TSelf"/>)
/// lets generic code constrain on it without boxing.
/// </summary>
public interface ILeveledTopologicalOrder<TSelf, N> : ITopologicalOrder<TSelf, N>
    where TSelf : ILeveledTopologicalOrder<TSelf, N>
    where N : notnull
{
    /// <summary>Level of <paramref name="node"/>: above every predecessor's level. Exact (0 without predecessors, else
    /// the longest path to it) until a removal; after removals levels stay valid but may be higher until
    /// <c>Tighten</c>.</summary>
    bool TryGetLevel(N node, out int level);

    /// <summary>Number of levels (<c>MaxLevel + 1</c>); 0 when empty.</summary>
    int LevelCount { get; }

    /// <summary>Number of nodes on <paramref name="level"/> (the size of that parallel group).</summary>
    int LevelWidth(int level);

    /// <summary>Recomputes exact (longest-path) levels: O(V + E) in topological order.</summary>
    TSelf Tighten<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct;
}

/// <summary>
/// A reserved node value meaning "no node", used to mark holes in an order's slot vector so slots need no separate
/// liveness flag (halving slot size for small ids and for references). The value can never be added as a node.
/// </summary>
public interface INodeSentinel<N>
{
    static abstract N None { get; }
    static abstract bool IsNone(N value);
}

/// <summary><c>null</c> for reference types; <c>default</c> (e.g. 0) for value types.</summary>
public readonly struct DefaultSentinel<N> : INodeSentinel<N>
{
    public static N None => default!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNone(N value) => typeof(N).IsValueType ? EqualityComparer<N>.Default.Equals(value, default!) : value is null;
}

/// <summary><c>N.MinValue</c> (e.g. <c>int.MinValue</c>), leaving 0 and every other id usable.</summary>
public readonly struct MinValueSentinel<N> : INodeSentinel<N> where N : IMinMaxValue<N>, IEqualityOperators<N, N, bool>
{
    public static N None => N.MinValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNone(N value) => value == N.MinValue;
}

/// <summary>A node of a topological order with its position, level and in-degree.</summary>
public readonly record struct TopoNodeInfo<N>(N Node, long Rank, int Level, int PredecessorCount);

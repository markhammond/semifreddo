using System.Numerics;
using Semifreddo.Internal;

namespace Semifreddo;

/// <summary>Creates empty <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/> orders. Each factory names its sentinel policy, the value
/// that marks "no node" in the order's slots, so no policy is chosen implicitly.</summary>
public static class ImmLeveledPkTopoOrder
{
    /// <summary>An empty order of numeric ids (<c>int</c>, <c>long</c>, …), <c>N.MinValue</c> marking "no node": 0 and
    /// every other id stay usable.</summary>
    public static ImmLeveledPkTopoOrder<N, MinValueSentinel<N>> CreateWithMinValueSentinel<N>()
        where N : notnull, IMinMaxValue<N>, IEqualityOperators<N, N, bool>
        => ImmLeveledPkTopoOrder<N, MinValueSentinel<N>>.Empty;

    /// <summary>An empty order of any id type, <c>default(N)</c> marking "no node": <c>null</c> for strings and other
    /// reference types. For a value type that value can't be an id (0 for <c>int</c>): give numeric ids
    /// <see cref="CreateWithMinValueSentinel{N}"/> instead.</summary>
    public static ImmLeveledPkTopoOrder<N, DefaultSentinel<N>> CreateWithDefaultSentinel<N>() where N : notnull
        => ImmLeveledPkTopoOrder<N, DefaultSentinel<N>>.Empty;
}

/// <summary>
/// <see cref="ImmPkTopoOrder{N, TSentinel}"/> plus levels (longest-path layers): nodes on the same level have no
/// dependency path between them, so each level is a parallel evaluation group. Levels are raised in topological order as
/// edges are added (each affected node at most once per operation); removals may leave them higher than necessary (still
/// valid) until <see cref="Tighten{TGraph}"/>. Costs over the order-only variant: 4 more bytes per node and a level pass
/// per out-of-order batch.
/// </summary>
/// <remarks>The members shared with <see cref="ImmPkTopoOrder{N, TSentinel}"/> are generated from one template by
/// <c>Semifreddo.Generators</c>; the level members below are specific to this type.</remarks>
[TopoOrderSurface]
public readonly partial struct ImmLeveledPkTopoOrder<N, TSentinel> : ILeveledTopologicalOrder<ImmLeveledPkTopoOrder<N, TSentinel>, N>, IImmutable,
    ITopoOrderStorage<ImmLeveledPkTopoOrder<N, TSentinel>, N, TSentinel, WithLevels>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
{
    /// <summary>Level of <paramref name="node"/>: above every predecessor's level. Exact (0 without predecessors, else
    /// the longest path to it) until a removal; after removals levels stay valid but may be higher until
    /// <see cref="Tighten{TGraph}"/>. Either way, nodes on the same level have no dependency path between them (a
    /// parallel evaluation group).</summary>
    public bool TryGetLevel(N node, out int level) => Core.TryGetLevelCore(node, out level);

    public int Level(N node)
    {
        if (!Core.TryGetLevelCore(node, out var l)) Throw.KeyNotFound(node);
        return l;
    }

    /// <summary>Number of levels (<c>MaxLevel + 1</c>); 0 when empty.</summary>
    public int LevelCount => Core.LevelCountCore;

    /// <summary>Deepest level; -1 when empty. The unit-cost critical path length in edges while levels are exact; after
    /// removals, an upper bound until <see cref="Tighten{TGraph}"/>.</summary>
    public int MaxLevel => Core.LevelCountCore - 1;

    /// <summary>Number of nodes on <paramref name="level"/> (the size of that parallel group).</summary>
    public int LevelWidth(int level) => Core.LevelWidthCore(level);

    /// <summary>Exact (longest-path) levels after removals left them loose: O(V + E) in topological order.</summary>
    public ImmLeveledPkTopoOrder<N, TSentinel> Tighten<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
        => new(Core.TightenCore(graph));

    /// <summary>
    /// Writes the nodes group by group into <paramref name="nodes"/> (length &gt;= Count), topological order within a
    /// group, and each group's start into <paramref name="levelStarts"/> (length &gt;= LevelCount + 1, last = Count).
    /// Group k is <c>nodes[levelStarts[k]..levelStarts[k + 1]]</c>. Allocation-free.
    /// </summary>
    public void CopyByLevel(Span<N> nodes, Span<int> levelStarts) => Core.CopyByLevelCore(nodes, levelStarts);

    /// <summary>As <see cref="CopyByLevel(Span{N}, Span{int})"/>, also filling each node's in-degree in
    /// <paramref name="graph"/>: the initial counters of a dependency-counting scheduler.</summary>
    public void CopyByLevel<TGraph>(TGraph graph, Span<N> nodes, Span<int> levelStarts, Span<int> predecessorCounts)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => Core.CopyByLevelCore(graph, nodes, levelStarts, predecessorCounts);

    /// <summary>The parallel evaluation groups (allocating convenience over <c>CopyByLevel</c>).</summary>
    public N[][] ToLevels() => Core.ToLevelsCore();

    /// <summary>Nodes in topological order with position, level and predecessor count (read from <paramref name="graph"/>).</summary>
    public DetailEnumerable<TGraph> WithDetails<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct => new(Core, graph);

    public readonly ref struct DetailEnumerable<TGraph> where TGraph : IDagAdjacency<N>, allows ref struct
    {
        private readonly TopoCore<N, TSentinel, WithLevels> _order;
        private readonly TGraph _graph;
        internal DetailEnumerable(TopoCore<N, TSentinel, WithLevels> order, TGraph graph) { _order = order; _graph = graph; }
        public DetailEnumerator<TGraph> GetEnumerator() => new(_order, _graph);
    }

    public ref struct DetailEnumerator<TGraph> where TGraph : IDagAdjacency<N>, allows ref struct
    {
        private TopoEnumerator<N, TSentinel> _e;
        private readonly TopoHead<N, WithLevels>? _h;
        private readonly TGraph _graph;

        internal DetailEnumerator(TopoCore<N, TSentinel, WithLevels> order, TGraph graph)
        {
            _e = order.GetEnumerator();
            _h = order.Head;
            _graph = graph;
        }

        public bool MoveNext() => _e.MoveNext();

        public readonly TopoNodeInfo<N> Current
        {
            get
            {
                var n = _e.Current;
                ref readonly var entry = ref _h!.Rank.GetValueRefOrNullRef(n);
                return new(n, (int)(entry.Rank - _h.Base), entry.Level, _graph.Predecessors(n).Count);
            }
        }
    }

    public sealed partial class Transient
    {
        /// <summary>Level of <paramref name="node"/> (see <see cref="ImmLeveledPkTopoOrder{N, TSentinel}.TryGetLevel"/>).</summary>
        public bool TryGetLevel(N node, out int level) => Core.TryGetLevelCore(node, out level);

        /// <summary>Number of levels; 0 when empty.</summary>
        public int LevelCount => Core.LevelCountCore;

        /// <summary>Number of nodes on <paramref name="level"/>.</summary>
        public int LevelWidth(int level) => Core.LevelWidthCore(level);

        /// <summary>Exact (longest-path) levels: O(V + E) in topological order.</summary>
        public void Tighten<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct => Core.TightenCore(graph);
    }
}

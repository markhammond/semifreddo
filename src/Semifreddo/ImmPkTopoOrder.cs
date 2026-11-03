using System.Numerics;

namespace Semifreddo;

/// <summary>Creates empty <see cref="ImmPkTopoOrder{N, TSentinel}"/> orders. Each factory names its sentinel policy, the value
/// that marks "no node" in the order's slots, so no policy is chosen implicitly.</summary>
public static class ImmPkTopoOrder
{
    /// <summary>An empty order of numeric ids (<c>int</c>, <c>long</c>, …), <c>N.MinValue</c> marking "no node": 0 and
    /// every other id stay usable.</summary>
    public static ImmPkTopoOrder<N, MinValueSentinel<N>> CreateWithMinValueSentinel<N>()
        where N : notnull, IMinMaxValue<N>, IEqualityOperators<N, N, bool>
        => ImmPkTopoOrder<N, MinValueSentinel<N>>.Empty;

    /// <summary>An empty order of any id type, <c>default(N)</c> marking "no node": <c>null</c> for strings and other
    /// reference types. For a value type that value can't be an id (0 for <c>int</c>): give numeric ids
    /// <see cref="CreateWithMinValueSentinel{N}"/> instead.</summary>
    public static ImmPkTopoOrder<N, DefaultSentinel<N>> CreateWithDefaultSentinel<N>() where N : notnull
        => ImmPkTopoOrder<N, DefaultSentinel<N>>.Empty;
}

/// <summary>
/// Persistent incremental topological order (Pearce &amp; Kelly 2006) of a DAG's nodes, maintained beside a graph it
/// does not own (any <see cref="IDagAdjacency{N}"/>). O(1) for edges that already agree with the order; otherwise a
/// bounded search over the affected window. Ranks are positions in an RRB vector (in-order iteration is a span scan;
/// holes from removals hold <typeparamref name="TSentinel"/>'s value and are trimmed / compacted), stored as wrap-safe
/// <c>uint</c>s. Single-reference readonly struct: tear-free, shareable across threads. For parallel evaluation groups
/// use <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/>.
/// </summary>
/// <remarks>The members shared with <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/> are generated from one template by
/// <c>Semifreddo.Generators</c> (<c>[TopoOrderSurface]</c>).</remarks>
[TopoOrderSurface]
public readonly partial struct ImmPkTopoOrder<N, TSentinel> : ITopologicalOrder<ImmPkTopoOrder<N, TSentinel>, N>, IImmutable,
    ITopoOrderStorage<ImmPkTopoOrder<N, TSentinel>, N, TSentinel, NoLevels>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>;

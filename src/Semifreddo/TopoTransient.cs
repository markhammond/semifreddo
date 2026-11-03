using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Semifreddo;

/// <summary>
/// Storage policy of a topological order's per-node entry: <see cref="NoLevels"/> (order only, 4-byte entries) or
/// <see cref="WithLevels"/> (order + parallel evaluation levels, 8-byte entries). The choice is a compile-time constant:
/// the order-only variant carries no level code or storage at all. Public only so <see cref="TopoTransient{TSelf, TOrder, N, TSentinel, TEntry}"/>
/// can name it; use <see cref="ImmPkTopoOrder{N, TSentinel}"/> / <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/>.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public interface ITopoEntry
{
    /// <summary>True if levels are maintained.</summary>
    static abstract bool TracksLevels { get; }

    /// <summary>Wrap-safe rank: <c>Base + position</c>.</summary>
    uint Rank { get; set; }

    /// <summary>Longest-path level (always 0 without levels).</summary>
    int Level { get; set; }
}

/// <summary>Order only: no level maintenance, smallest entries.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public struct NoLevels : ITopoEntry
{
    /// <inheritdoc/>
    public static bool TracksLevels => false;

    /// <inheritdoc/>
    public uint Rank { get; set; }

    /// <inheritdoc/>
    public int Level { readonly get => 0; set { } }
}

/// <summary>Order plus levels (longest-path layers).</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public struct WithLevels : ITopoEntry
{
    /// <inheritdoc/>
    public static bool TracksLevels => true;

    /// <inheritdoc/>
    public uint Rank { get; set; }

    /// <inheritdoc/>
    public int Level { get; set; }
}

/// <summary>
/// Binds a persistent order type to its engine (CRTP). Its members are internal, so only this library's orders can
/// implement it; it exists so <see cref="TopoTransient{TSelf, TOrder, N, TSentinel, TEntry}"/> can freeze to and reset from
/// <typeparamref name="TSelf"/> without per-type code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface ITopoOrderStorage<TSelf, N, TSentinel, TEntry>
    where TSelf : struct, ITopoOrderStorage<TSelf, N, TSentinel, TEntry>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
    where TEntry : struct, ITopoEntry
{
    internal static abstract TSelf Wrap(TopoCore<N, TSentinel, TEntry> core);
    internal static abstract TopoCore<N, TSentinel, TEntry> Unwrap(TSelf order);
}

/// <summary>Heap-safe state machine behind <see cref="TopoEnumerator{N, TSentinel}"/> and the <c>IEnumerable</c> path.</summary>
internal struct TopoCursor<N, TSentinel> : IEnumerator<N>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
{
    private ImmFlexVector<N>.Cursor _e;
    internal TopoCursor(ImmFlexVector<N> slots) => _e = slots.GetCursor();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (_e.MoveNext())
            if (!TSentinel.IsNone(_e.Current)) return true;
        return false;
    }

    public readonly N Current => _e.Current;
    readonly object IEnumerator.Current => Current;
    public void Reset() => _e.Reset();
    public readonly void Dispose() { }
}

/// <summary>
/// Nodes of a topological order in order (span-chunked scan, removal holes skipped). A duck-typed <c>foreach</c>
/// enumerator and a <c>ref struct</c>: it can never be boxed (the <see cref="IEnumerable{T}"/> path uses a separate cursor).
/// </summary>
public ref struct TopoEnumerator<N, TSentinel> : IEnumerator<N>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
{
    private ImmFlexVector<N>.Enumerator _e;   // span-based: holes are skipped within the current leaf span
    internal TopoEnumerator(ImmFlexVector<N> slots) => _e = slots.GetEnumerator();

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (_e.MoveNext())
            if (!TSentinel.IsNone(_e.Current)) return true;
        return false;
    }

    /// <inheritdoc/>
    public readonly N Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _e.Current;
    }

    readonly object IEnumerator.Current => Current;

    /// <inheritdoc/>
    public void Reset() => _e.Reset();

    /// <inheritdoc/>
    public readonly void Dispose() { }
}

/// <summary>
/// Shared transient surface of <see cref="ImmPkTopoOrder{N, TSentinel}.Transient"/> and
/// <see cref="ImmLeveledPkTopoOrder{N, TSentinel}.Transient"/> (CRTP: <typeparamref name="TSelf"/> is the derived transient,
/// <typeparamref name="TOrder"/> the persistent order it freezes to). Write generic code against it with
/// <c>where TT : TopoTransient&lt;TT, TOrder, N, TSentinel, TEntry&gt;</c>; it cannot be derived from outside this library.
/// <para>Mutable view: searches reuse scratch buffers held here (no per-call allocation once warm). Not thread-safe.
/// After a failed <c>TryAddEdge(s)</c> the order (and levels) are unspecified: <see cref="Reset"/> to roll back the
/// whole batch before using it again. The persistent orders are unaffected: a failed edit returns the original.</para>
/// </summary>
public abstract class TopoTransient<TSelf, TOrder, N, TSentinel, TEntry>
    where TSelf : TopoTransient<TSelf, TOrder, N, TSentinel, TEntry>, new()
    where TOrder : struct, ITopoOrderStorage<TOrder, N, TSentinel, TEntry>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
    where TEntry : struct, ITopoEntry
{
    internal readonly TopoCore<N, TSentinel, TEntry>.Transient Core = new();

    [ThreadStatic] private static TSelf? t_cache;

    private protected TopoTransient() { }

    internal static TSelf Rent(TopoCore<N, TSentinel, TEntry> source)
    {
        var t = t_cache;
        if (t is null) t = new TSelf();
        else t_cache = null;
        t.Core.Reset(source);
        return t;
    }

    internal static void Return(TSelf t)
    {
        t.Core.Reset(default);
        t.Core.TrimScratch();
        t_cache = t;
    }

    /// <summary>Number of nodes.</summary>
    public int Count => Core.Count;

    /// <summary>True if <paramref name="node"/> is in the order.</summary>
    public bool Contains(N node) => Core.Contains(node);

    /// <summary>Position of <paramref name="node"/>: comparable within the current state only.</summary>
    public bool TryGetRank(N node, out long rank) => Core.TryGetRank(node, out rank);

    /// <summary>Appends a node (last in the order); false if present. The sentinel value is rejected.</summary>
    public bool Add(N node) => Core.Add(node);

    /// <summary>Removes a node; false if absent. Never invalidates the order of the others.</summary>
    public bool Remove(N node) => Core.Remove(node);

    /// <summary>Gives <paramref name="newNode"/> the position (and level) of <paramref name="oldNode"/> (rename).</summary>
    public bool Replace(N oldNode, N newNode) => Core.Replace(oldNode, newNode);

    /// <summary>Restores the order after <paramref name="graph"/> gained from→to; false and one cycle on failure.</summary>
    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => Core.TryAddEdge(graph, from, to, out cycle);

    /// <summary>Probe: false on a cycle without building it (allocation-free failure path).</summary>
    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to) where TGraph : IDagAdjacency<N>, allows ref struct
        => Core.TryAddEdge(graph, from, to);

    /// <summary>Restores the order after <paramref name="graph"/> gained all <paramref name="edges"/>; false and one
    /// cycle on failure.</summary>
    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => Core.TryAddEdges(graph, edges, out cycle);

    /// <summary>Probe form of the batch overload.</summary>
    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges) where TGraph : IDagAdjacency<N>, allows ref struct
        => Core.TryAddEdges(graph, edges);

    /// <summary>Discards all edits: the transient now holds <paramref name="source"/>.</summary>
    public void Reset(TOrder source) => Core.Reset(TOrder.Unwrap(source));

    /// <summary>O(1) freeze; the transient remains usable (later writes copy-on-write).</summary>
    public TOrder ToPersistent() => TOrder.Wrap(Core.ToPersistent());

    /// <summary>Nodes in topological order (of a frozen snapshot).</summary>
    public TopoEnumerator<N, TSentinel> GetEnumerator() => Core.GetEnumerator();

    /// <summary>Renumbers densely from <paramref name="newBase"/> (tests use it to exercise uint wrap-around).</summary>
    internal void Rebase(uint newBase) => Core.Rebase(newBase);
}

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Semifreddo.Internal;

namespace Semifreddo;

/// <summary>Payload type for an <see cref="ImmDag{N, A, V}"/> whose nodes carry no value.</summary>
public readonly record struct NoPayload;

/// <summary>
/// Immutable adjacency set of a DAG node. Sparse graphs dominate, so up to <see cref="MaxInline"/> neighbours are
/// held in an exact-size array (one object, linear scan, span access); larger degrees switch to a CHAMP set, which a
/// transient edits in place (owned nodes, spare capacity trimmed at freeze). Back to an array at half capacity.
/// Single reference field.
/// </summary>
public readonly struct EdgeSet<N> : IReadOnlyCollection<N>, IRefEnumerable<N, EdgeSet<N>.Enumerator> where N : notnull
{
    internal const int MaxInline = 16;

    private readonly object? _o; // null | N[] | ChampNode<SetEntry<N>> (CHAMP root; Count maintained on the root)

    private EdgeSet(object? o) => _o = o;

    internal object? Raw => _o; // tests: storage inspection

    public int Count => _o switch
    {
        null => 0,
        N[] a => a.Length,
        _ => Unsafe.As<ChampNode<SetEntry<N>>>(_o).Count,
    };

    public bool IsEmpty => _o is null;

    /// <summary>The neighbours as a span when stored inline (degree &lt;= 16); false for large sets.</summary>
    public bool TryGetSpan(out ReadOnlySpan<N> span)
    {
        if (_o is ChampNode<SetEntry<N>>) { span = default; return false; }
        span = (N[]?)_o;
        return true;
    }

    public bool Contains(N node) => _o switch
    {
        null => false,
        N[] a => IndexOf(a, node) >= 0,
        _ => !Unsafe.IsNullRef(ref Set.Find(Unsafe.As<ChampNode<SetEntry<N>>>(_o), node)),
    };

    private static int IndexOf(N[] a, N node)
    {
        for (var i = 0; i < a.Length; i++)
            if (EqualityComparer<N>.Default.Equals(a[i], node)) return i;
        return -1;
    }

    private static class Set
    {
        public static ref SetEntry<N> Find(ChampNode<SetEntry<N>> root, N node)
            => ref Champ<SetEntry<N>, N, DefaultHasher<N>>.Find(root, node);
    }

    /// <summary>Adds <paramref name="node"/>; with an owner token, CHAMP nodes created or owned by it change in place.</summary>
    internal EdgeSet<N> Add(N node, ChampOwner<SetEntry<N>>? o, out bool added)
    {
        switch (_o)
        {
            case null:
                added = true;
                return new(new[] { node });
            case N[] a:
                if (IndexOf(a, node) >= 0) { added = false; return this; }
                added = true;
                if (a.Length < MaxInline)
                {
                    var r = new N[a.Length + 1];
                    a.CopyTo(r, 0);
                    r[^1] = node;
                    return new(r);
                }
                return new(Promote(a, node, o));
            default:
                var root = Unsafe.As<ChampNode<SetEntry<N>>>(_o);
                if (!Unsafe.IsNullRef(ref Set.Find(root, node))) { added = false; return this; }
                added = true;
                return new(SetAdd(root, node, o));
        }
    }

    private static ChampNode<SetEntry<N>> Promote(N[] a, N extra, ChampOwner<SetEntry<N>>? o)
    {
        if (o is null) return ImmSet.Create<N>(a).Add(extra).Root!;
        var root = new ChampNode<SetEntry<N>>(0, 0, [], [], o);
        foreach (var x in a) Champ<SetEntry<N>, N, DefaultHasher<N>>.GetOrAdd(ref root, x, o, out _);
        Champ<SetEntry<N>, N, DefaultHasher<N>>.GetOrAdd(ref root, extra, o, out _);
        root.Count = a.Length + 1;
        return root;
    }

    private static ChampNode<SetEntry<N>> SetAdd(ChampNode<SetEntry<N>> root, N node, ChampOwner<SetEntry<N>>? o)
    {
        if (o is null) return new ImmSet<N>(root).Add(node).Root!;
        var c = root.Count;
        var slot = root;
        Champ<SetEntry<N>, N, DefaultHasher<N>>.GetOrAdd(ref slot, node, o, out _);
        slot.Count = c + 1; // slot is owned now (the same node, or its owned clone)
        return slot;
    }

    internal EdgeSet<N> Remove(N node, ChampOwner<SetEntry<N>>? o, out bool removed)
    {
        switch (_o)
        {
            case null:
                removed = false;
                return this;
            case N[] a:
                var i = IndexOf(a, node);
                if (i < 0) { removed = false; return this; }
                removed = true;
                if (a.Length == 1) return default;
                var r = new N[a.Length - 1];
                a.AsSpan(0, i).CopyTo(r);
                a.AsSpan(i + 1).CopyTo(r.AsSpan(i));
                return new(r);
            default:
                var root = Unsafe.As<ChampNode<SetEntry<N>>>(_o);
                if (Unsafe.IsNullRef(ref Set.Find(root, node))) { removed = false; return this; }
                removed = true;
                var c = root.Count;
                ChampNode<SetEntry<N>> nr;
                if (o is null)
                {
                    nr = new ImmSet<N>(root).Remove(node).Root!;
                }
                else
                {
                    var rm = false;
                    nr = Champ<SetEntry<N>, N, DefaultHasher<N>>.Remove(root, node, Champ<SetEntry<N>, N, DefaultHasher<N>>.HashOf(node), 0, o, ref rm);
                    nr.Count = c - 1;
                }
                if (c - 1 > MaxInline / 2) return new(nr);
                var arr = new N[c - 1]; // hysteresis: back to inline at half capacity
                var k = 0;
                var it = new ChampIterator<SetEntry<N>>(nr);
                while (it.MoveNext()) arr[k++] = it.Current.Key;
                return new(arr);
        }
    }

    public Enumerator GetEnumerator() => new(_o);
    internal Cursor GetCursor() => new(_o);
    IEnumerator<N> IEnumerable<N>.GetEnumerator() => GetCursor();
    IEnumerator IEnumerable.GetEnumerator() => GetCursor();

    bool IRefEnumerable<N, Enumerator>.TryGetSpan(out ReadOnlySpan<N> span)
    {
        span = _o as N[];
        return _o is null or N[];
    }

    bool IRefEnumerable<N, Enumerator>.TryCopyTo(int index, Span<N> destination)
    {
        var n = RefEnumerable.CopyLength(Count, index, destination.Length);
        if (_o is not N[] a) return _o is null;
        a.AsSpan(index, n).CopyTo(destination);
        return true;
    }

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<N>
    {
        private readonly object? _o;
        private readonly N[]? _a;
        private ChampIterator<SetEntry<N>> _s;
        private int _i;

        internal Cursor(object? o)
        {
            _o = o;
            _a = o as N[];
            _s = new(o as ChampNode<SetEntry<N>>);
            _i = -1;
        }

        public bool MoveNext() => _a is not null ? ++_i < _a.Length : _s.MoveNext();

        public readonly N Current => _a is not null ? _a[_i] : _s.Current.Key;
        object IEnumerator.Current => Current;
        public void Reset() => this = new(_o);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c> that walks the inline array as a span, or the CHAMP
    /// set node by node as spans. It can never be boxed; the <see cref="IEnumerable{T}"/> path hands out a separate
    /// heap-safe cursor instead.
    /// </summary>
    public ref struct Enumerator : IEnumerator<N>
    {
        private readonly object? _o;
        private EdgeSpanIterator<N> _it;

        internal Enumerator(object? o)
        {
            _o = o;
            _it = EdgeSpanIterator<N>.Of(o);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _it.MoveNext();

        public readonly N Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _it.Current;
        }

        readonly object? IEnumerator.Current => Current;
        public void Reset() => _it = EdgeSpanIterator<N>.Of(_o);
        public readonly void Dispose() { }
    }
}

internal struct DagNode<N, A, V> where N : notnull
{
    public A Alt;
    public V Value;
    public EdgeSet<N> Out;
    public EdgeSet<N> In;
}

internal sealed class DagHead<N, A, V> where N : notnull where A : notnull
{
    public ImmMap<N, DagNode<N, A, V>> Nodes;
    public ImmMap<A, N> ByAlt;
    public int Edges;
}

/// <summary>A node of an <see cref="ImmDag{N, A, V}"/> with its alternate key, payload and adjacency.</summary>
public readonly record struct DagNodeView<N, A, V>(N Id, A AlternateKey, V Value, EdgeSet<N> Successors, EdgeSet<N> Predecessors)
    where N : notnull;


/// <summary>
/// How a DAG batch commits. The defaults are the safe ones: the batch is checked for cycles and records its change
/// log. Pass <c>new() { SkipCycleCheck = true }</c> (say) to opt out.
/// </summary>
public readonly record struct DagBatchOptions
{
    /// <summary>
    /// Commit without proving the batch acyclic (default false), for callers that have proved it themselves. Prefer
    /// the overloads that take a topological order (<c>Mutate(ref order, …)</c>), which prove it for you, in O(1) for
    /// edges that agree with the order. Committing a cycle breaks the DAG invariant; nothing downstream re-checks it.
    /// </summary>
    public bool SkipCycleCheck { get; init; }

    /// <summary>
    /// Don't record the batch's change log (default false): saves its bookkeeping, and the capacity it grows with the
    /// batch. Reading <see cref="ImmDag{N, A, V}.Scope.AddedEdges"/> and the rest of the log then throws. The cycle
    /// check, if not skipped, still works (it keeps just the added edges' targets).
    /// </summary>
    public bool SkipChangeLog { get; init; }
}

/// <summary>Thrown when a batch would introduce a cycle. <see cref="Cycle"/> is one offending cycle (first == last).</summary>
/// <remarks>The message, which lists the cycle, is built when first read: a caller that catches and inspects
/// <see cref="Cycle"/> never pays for it.</remarks>
public sealed class DagCycleException<N>(N[] cycle) : InvalidOperationException
{
    private string? _message;

    public N[] Cycle { get; } = cycle;

    public override string Message => _message ??= $"Batch introduces a cycle: {string.Join(" -> ", Cycle)}";
}

/// <summary>Creates empty <see cref="ImmDag{N, A, V}"/> graphs.</summary>
public static class ImmDag
{
    /// <summary>An empty graph whose nodes carry no payload (<see cref="NoPayload"/>): ids and alternate keys only.</summary>
    public static ImmDag<N, A, NoPayload> Create<N, A>() where N : notnull where A : notnull => ImmDag<N, A, NoPayload>.Empty;

    /// <summary>An empty graph whose nodes carry a <typeparamref name="V"/> payload.</summary>
    public static ImmDag<N, A, V> Create<N, A, V>() where N : notnull where A : notnull => ImmDag<N, A, V>.Empty;
}

/// <summary>
/// Persistent directed acyclic graph. Each node has an id <typeparamref name="N"/>, a one-to-one alternate key
/// <typeparamref name="A"/> (id ⇄ key), a payload <typeparamref name="V"/> (<see cref="NoPayload"/> if unused) and
/// successor / predecessor sets. Acyclicity is validated once per batch (at <c>ToPersistent</c> / end of
/// <c>Mutate</c>) over only the sub-graph reachable from edges the batch added; a failed batch leaves the source value
/// untouched (free rollback) and reports one offending cycle. Each batch keeps a net change log and its origin version,
/// so layers such as deferred reclamation of removed payloads can be built on top. Layout: CHAMP maps id → node and
/// key → id under one header, i.e. a single CAS-able reference.
/// <para>Enumeration is in hash order — neither insertion nor topological order; maintain a
/// topological order with <see cref="ImmPkTopoOrder{N, TSentinel}"/>.</para>
/// </summary>
public readonly struct ImmDag<N, A, V> : IReadOnlyCollection<DagNodeView<N, A, V>>, IDagAdjacency<N>, IImmutable,
    IRefEnumerable<DagNodeView<N, A, V>, ImmDag<N, A, V>.Enumerator>
    where N : notnull
    where A : notnull
{
    private readonly DagHead<N, A, V>? _h;

    internal ImmDag(DagHead<N, A, V>? h) => _h = h is null || h.Nodes.IsEmpty ? null : h;

    internal DagHead<N, A, V>? Head => _h;

    public static ImmDag<N, A, V> Empty => default;

    public int NodeCount => _h is null ? 0 : _h.Nodes.Count;
    int IReadOnlyCollection<DagNodeView<N, A, V>>.Count => NodeCount;
    public int EdgeCount => _h?.Edges ?? 0;
    public bool IsEmpty => _h is null || _h.Nodes.IsEmpty;
    public bool IsSameAs(ImmDag<N, A, V> other) => ReferenceEquals(_h, other._h);

    public bool ContainsNode(N id) => _h is not null && _h.Nodes.ContainsKey(id);

    public bool ContainsEdge(N from, N to)
    {
        if (_h is null) return false;
        ref readonly var e = ref _h.Nodes.GetValueRefOrNullRef(from);
        return !Unsafe.IsNullRef(in e) && e.Out.Contains(to);
    }

    public bool TryGetAlternateKey(N id, [MaybeNullWhen(false)] out A alternateKey)
    {
        alternateKey = default;
        if (_h is null) return false;
        ref readonly var e = ref _h.Nodes.GetValueRefOrNullRef(id);
        if (Unsafe.IsNullRef(in e)) return false;
        alternateKey = e.Alt;
        return true;
    }

    public bool TryGetNodeByAlternateKey(A alternateKey, [MaybeNullWhen(false)] out N id)
    {
        if (_h is not null) return _h.ByAlt.TryGetValue(alternateKey, out id);
        id = default;
        return false;
    }

    public bool TryGetValue(N id, [MaybeNullWhen(false)] out V value)
    {
        value = default;
        if (_h is null) return false;
        ref readonly var e = ref _h.Nodes.GetValueRefOrNullRef(id);
        if (Unsafe.IsNullRef(in e)) return false;
        value = e.Value;
        return true;
    }

    public bool TryGetNode(N id, out DagNodeView<N, A, V> node)
    {
        node = default;
        if (_h is null) return false;
        ref readonly var e = ref _h.Nodes.GetValueRefOrNullRef(id);
        if (Unsafe.IsNullRef(in e)) return false;
        node = new(id, e.Alt, e.Value, e.Out, e.In);
        return true;
    }

    public EdgeSet<N> Successors(N id) => Node(id).Out;
    public EdgeSet<N> Predecessors(N id) => Node(id).In;
    Adjacency<N> IDagAdjacency<N>.Successors(N node) => new(Node(node).Out);
    Adjacency<N> IDagAdjacency<N>.Predecessors(N node) => new(Node(node).In);

    private ref readonly DagNode<N, A, V> Node(N id)
    {
        if (_h is null) Throw.KeyNotFound(id);
        ref readonly var e = ref _h.Nodes.GetValueRefOrNullRef(id);
        if (Unsafe.IsNullRef(in e)) Throw.KeyNotFound(id);
        return ref e;
    }

    // ------------------------------------------------------------------------------------------------ single edits

    // Single edits run through the pooled transient, so multi-entry edits (edge = 2 nodes, node removal or rename =
    // 1 + degree nodes) share one path-copy per touched path. They keep no change log (nothing can read it), and an
    // edge is proved acyclic before it is added, so a refusal edits nothing.
    private static readonly DagBatchOptions Unlogged = new() { SkipChangeLog = true };
    private static readonly DagBatchOptions Prechecked = new() { SkipChangeLog = true, SkipCycleCheck = true };

    public ImmDag<N, A, V> AddNode(N id, A alternateKey, V value)
        => Mutate((id, alternateKey, value), static (s, a) =>
        {
            if (!s.AddNode(a.id, a.alternateKey, a.value)) Throw.NodePresent(a.id, a.alternateKey);
        }, Unlogged);

    public ImmDag<N, A, V> AddNode(N id, A alternateKey) => AddNode(id, alternateKey, default!);

    /// <summary>Removes the node and its incident edges.</summary>
    public ImmDag<N, A, V> RemoveNode(N id) => ContainsNode(id) ? Mutate(id, static (s, n) => s.RemoveNode(n), Unlogged) : this;

    public ImmDag<N, A, V> SetValue(N id, V value)
        => Mutate((id, value), static (s, a) => { if (!s.SetValue(a.id, a.value)) Throw.KeyNotFound(a.id); }, Unlogged);

    /// <summary>Renames a node keeping its alternate key, payload and every edge (O(degree)).</summary>
    public ImmDag<N, A, V> ReplaceNode(N oldId, N newId)
        => Mutate((oldId, newId), static (s, a) => { if (!s.ReplaceNode(a.oldId, a.newId)) Throw.CannotReplace(a.oldId, a.newId); }, Unlogged);

    /// <summary>Replaces a node's id, alternate key and payload keeping every edge (O(degree)).</summary>
    public ImmDag<N, A, V> ReplaceNode(N oldId, N newId, A alternateKey, V value)
        => Mutate((oldId, newId, alternateKey, value), static (s, a) =>
        {
            if (!s.ReplaceNode(a.oldId, a.newId, a.alternateKey, a.value)) Throw.CannotReplace(a.oldId, a.newId);
        }, Unlogged);

    /// <summary>
    /// Adds an edge, throwing <see cref="DagCycleException{N}"/> if it would close a cycle (where a cycle is expected,
    /// prefer <see cref="TryAddEdge(N, N, out ImmDag{N, A, V}, out N[])"/>: no exception). Cost: proving acyclicity
    /// without an order walks everything reachable from <paramref name="to"/> — O(reachable sub-graph), milliseconds
    /// on large graphs. On hot paths keep a topological order beside the graph and use
    /// <see cref="AddEdge{TOrder}(ref TOrder, N, N)"/> (O(1) for edges that agree with it), or batch many edges into one
    /// <see cref="Mutate{TArg}(TArg, Action{Scope, TArg})"/> (one walk per batch, not per edge).
    /// </summary>
    public ImmDag<N, A, V> AddEdge(N from, N to)
    {
        if (!TryAddEdge(from, to, out var r, out var cycle)) Throw.Cycle(cycle);
        return r;
    }

    /// <summary>
    /// Adds an edge unless it would close a cycle: then returns false, <paramref name="result"/> = this, and one cycle
    /// (first == last). The graph is searched from <paramref name="to"/> for <paramref name="from"/> before anything is
    /// edited, so a refusal costs only that search. An edge already present returns true and this version.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either node is absent.</exception>
    public bool TryAddEdge(N from, N to, out ImmDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
    {
        result = this;
        var t = Transient.Rent(_h, Prechecked);
        try
        {
            if (!t.TryAddEdgeAcyclic(from, to, out cycle)) return false;
            result = t.ToPersistent(skipCycleCheck: true);
            return true;
        }
        finally { Transient.Return(t); }
    }

    public ImmDag<N, A, V> RemoveEdge(N from, N to) => ContainsEdge(from, to) ? Mutate((from, to), static (s, e) => s.RemoveEdge(e.from, e.to), Unlogged) : this;

    // ------------------------------------------------------------------------------------------------ batches

    /// <summary>Applies a batch; validates acyclicity once at the end (unless <see cref="DagBatchOptions.SkipCycleCheck"/>).
    /// Throws <see cref="DagCycleException{N}"/> (this value is unchanged) if the batch introduces a cycle.</summary>
    public ImmDag<N, A, V> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
        => Mutate(arg, batch, default(DagBatchOptions));

    /// <inheritdoc cref="Mutate{TArg}(TArg, Action{Scope, TArg})"/>
    public ImmDag<N, A, V> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch, DagBatchOptions options) where TArg : allows ref struct
    {
        var t = Transient.Rent(_h, options);
        try { batch(new Scope(t), arg); return t.ToPersistent(options.SkipCycleCheck); }
        finally { Transient.Return(t); }
    }

    /// <inheritdoc cref="Mutate{TArg}(TArg, Action{Scope, TArg})"/>
    public ImmDag<N, A, V> Mutate(Action<Scope> batch) => Mutate(batch, default(DagBatchOptions));

    /// <inheritdoc cref="Mutate{TArg}(TArg, Action{Scope, TArg})"/>
    public ImmDag<N, A, V> Mutate(Action<Scope> batch, DagBatchOptions options)
    {
        var t = Transient.Rent(_h, options);
        try { batch(new Scope(t)); return t.ToPersistent(options.SkipCycleCheck); }
        finally { Transient.Return(t); }
    }

    /// <summary>Non-throwing variant: on a cycle (a self-loop included) returns false,
    /// <paramref name="result"/> = this, and the cycle.</summary>
    public bool TryMutate<TArg>(TArg arg, Action<Scope, TArg> batch, out ImmDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
        where TArg : allows ref struct
        => TryMutate(arg, batch, default, out result, out cycle);

    /// <inheritdoc cref="TryMutate{TArg}(TArg, Action{Scope, TArg}, out ImmDag{N, A, V}, out N[])"/>
    public bool TryMutate<TArg>(TArg arg, Action<Scope, TArg> batch, DagBatchOptions options, out ImmDag<N, A, V> result,
        [NotNullWhen(false)] out N[]? cycle) where TArg : allows ref struct
    {
        var t = Transient.Rent(_h, options);
        try
        {
            batch(new Scope(t), arg);
            if (options.SkipCycleCheck)
            {
                if (t.SelfLoop is { } loop) { result = this; cycle = loop; return false; }
                result = t.ToPersistent(skipCycleCheck: true);
                cycle = null;
                return true;
            }
            if (t.TryToPersistent(out result, out cycle)) return true;
            result = this;
            return false;
        }
        finally { Transient.Return(t); }
    }

    // ------------------------------------------------------------------------------------------------ with an order

    /// <summary>
    /// Applies a batch and keeps <paramref name="order"/>, a topological order of this graph's nodes, in step with it:
    /// removed nodes leave the order, added ones join it, and the added edges are placed by the order itself
    /// (Pearce–Kelly: O(1) for an edge that agrees with it, else a search bounded by the affected window). That proves
    /// the batch acyclic, so the graph is not searched again. On a cycle (including a self-loop) returns false with it,
    /// and neither <paramref name="result"/> (= this) nor <paramref name="order"/> changes. Removed edges never
    /// invalidate an order (a levelled order's levels stay valid, if no longer tight: see its <c>Tighten</c>).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="order"/> does not hold as many nodes as this graph (it
    /// must hold exactly its nodes).</exception>
    public bool TryMutate<TOrder, TArg>(ref TOrder order, TArg arg, Action<Scope, TArg> batch, out ImmDag<N, A, V> result,
        [NotNullWhen(false)] out N[]? cycle)
        where TOrder : ITopologicalOrder<TOrder, N> where TArg : allows ref struct
    {
        CheckOrder(in order);
        var t = Transient.Rent(_h, default);
        try
        {
            batch(new Scope(t), arg);
            if (t.SelfLoop is { } loop) { result = this; cycle = loop; return false; }
            var next = order;
            foreach (var id in t.RemovedNodes) next = next.Remove(id);   // net, by id: renames are a removal and an addition
            foreach (var id in t.AddedNodes) next = next.Add(id);
            var added = t.AddedEdges;
            if (!added.IsEmpty && !next.TryAddEdges(t, added, out next, out cycle)) { result = this; return false; }
            result = t.ToPersistent(skipCycleCheck: true);
            order = next;
            cycle = null;
            return true;
        }
        finally { Transient.Return(t); }
    }

    /// <summary>As <see cref="TryMutate{TOrder, TArg}(ref TOrder, TArg, Action{Scope, TArg}, out ImmDag{N, A, V}, out N[])"/>,
    /// throwing <see cref="DagCycleException{N}"/> on a cycle (nothing changes).</summary>
    public ImmDag<N, A, V> Mutate<TOrder, TArg>(ref TOrder order, TArg arg, Action<Scope, TArg> batch)
        where TOrder : ITopologicalOrder<TOrder, N> where TArg : allows ref struct
        => TryMutate(ref order, arg, batch, out var r, out var cycle) ? r : throw new DagCycleException<N>(cycle);

    /// <inheritdoc cref="Mutate{TOrder, TArg}(ref TOrder, TArg, Action{Scope, TArg})"/>
    public ImmDag<N, A, V> Mutate<TOrder>(ref TOrder order, Action<Scope> batch) where TOrder : ITopologicalOrder<TOrder, N>
        => Mutate(ref order, batch, static (s, b) => b(s));

    /// <summary>Adds an edge and places it in <paramref name="order"/> (O(1) if it agrees with the order); throws
    /// <see cref="DagCycleException{N}"/> if it would close a cycle (nothing changes).</summary>
    public ImmDag<N, A, V> AddEdge<TOrder>(ref TOrder order, N from, N to) where TOrder : ITopologicalOrder<TOrder, N>
    {
        if (!TryAddEdge(ref order, from, to, out var r, out var cycle)) Throw.Cycle(cycle);
        return r;
    }

    /// <summary>
    /// Adds an edge and places it in <paramref name="order"/> (O(1) if it agrees with the order). If it would close a
    /// cycle, returns false, <paramref name="result"/> = this, and one cycle (first == last); <paramref name="order"/>
    /// is unchanged. An edge already present returns true, and neither changes.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Either node is absent.</exception>
    /// <exception cref="ArgumentException"><paramref name="order"/> does not hold exactly this graph's nodes.</exception>
    public bool TryAddEdge<TOrder>(ref TOrder order, N from, N to, out ImmDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
        where TOrder : ITopologicalOrder<TOrder, N>
    {
        CheckOrder(in order);
        result = this;
        ref readonly var f = ref Node(from);
        Node(to);
        if (EqualityComparer<N>.Default.Equals(from, to)) { cycle = [from, to]; return false; }
        cycle = null;
        if (f.Out.Contains(to)) return true;                          // present: nothing changes
        if (!order.TryAddEdge(new WithEdge(this, from, to), from, to, out var next, out cycle)) return false;
        var t = Transient.Rent(_h, Prechecked);                       // the order proved it: edit only now
        try
        {
            t.AddEdge(from, to);
            result = t.ToPersistent(skipCycleCheck: true);
        }
        finally { Transient.Return(t); }
        order = next;
        return true;
    }

    /// <summary>
    /// This version as if it held from → to, for an order to search before anything is edited: a refusal then leaves
    /// nothing to undo. Only the two neighbour sets the edge changes are copied, and only if the search reads them.
    /// </summary>
    private readonly ref struct WithEdge(ImmDag<N, A, V> graph, N from, N to) : IDagAdjacency<N>
    {
        public Adjacency<N> Successors(N node)
            => EqualityComparer<N>.Default.Equals(node, from) ? new(Plus(graph.Successors(node), to)) : new(graph.Successors(node));

        public Adjacency<N> Predecessors(N node)
            => EqualityComparer<N>.Default.Equals(node, to) ? new(Plus(graph.Predecessors(node), from)) : new(graph.Predecessors(node));

        private static ReadOnlySpan<N> Plus(EdgeSet<N> set, N extra)
        {
            var a = new N[set.Count + 1];
            var i = 0;
            foreach (var n in set) a[i++] = n;
            a[i] = extra;
            return a;
        }
    }

    /// <summary>Adds a node, and adds it to <paramref name="order"/>.</summary>
    public ImmDag<N, A, V> AddNode<TOrder>(ref TOrder order, N id, A alternateKey, V value) where TOrder : ITopologicalOrder<TOrder, N>
    {
        CheckOrder(in order);
        var r = AddNode(id, alternateKey, value);
        order = order.Add(id);
        return r;
    }

    /// <summary>Removes a node and its incident edges, and removes it from <paramref name="order"/>.</summary>
    public ImmDag<N, A, V> RemoveNode<TOrder>(ref TOrder order, N id) where TOrder : ITopologicalOrder<TOrder, N>
    {
        if (!ContainsNode(id)) return this;
        CheckOrder(in order);
        var r = RemoveNode(id);
        order = order.Remove(id);
        return r;
    }

    private void CheckOrder<TOrder>(in TOrder order) where TOrder : ITopologicalOrder<TOrder, N>
    {
        if (order.Count != NodeCount) Throw.OrderMismatch(order.Count, NodeCount);
    }

    public Transient ToTransient()
    {
        var t = new Transient();
        t.Reset(this);
        return t;
    }

    /// <summary>Nodes in hash order (see the type remarks).</summary>
    public Enumerator GetEnumerator() => new(_h);
    internal Cursor GetCursor() => new(_h);
    IEnumerator<DagNodeView<N, A, V>> IEnumerable<DagNodeView<N, A, V>>.GetEnumerator() => GetCursor();
    IEnumerator IEnumerable.GetEnumerator() => GetCursor();

    // Hash-ordered storage: no contiguous span and no positional access, so callers enumerate.
    bool IRefEnumerable<DagNodeView<N, A, V>, Enumerator>.TryGetSpan(out ReadOnlySpan<DagNodeView<N, A, V>> span)
    {
        span = default;
        return NodeCount == 0;
    }

    bool IRefEnumerable<DagNodeView<N, A, V>, Enumerator>.TryCopyTo(int index, Span<DagNodeView<N, A, V>> destination)
    {
        RefEnumerable.CopyLength(NodeCount, index, destination.Length);
        return false;
    }

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<DagNodeView<N, A, V>>
    {
        private ImmMap<N, DagNode<N, A, V>>.Cursor _e;

        internal Cursor(DagHead<N, A, V>? h) => _e = (h?.Nodes ?? default).GetCursor();

        public bool MoveNext() => _e.MoveNext();

        public readonly DagNodeView<N, A, V> Current
        {
            get
            {
                ref readonly var n = ref _e.CurrentValue;
                return new(_e.CurrentKey, n.Alt, n.Value, n.Out, n.In);
            }
        }

        object IEnumerator.Current => Current;
        public void Reset() => _e.Reset();
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c>, so it can never be boxed or captured. The
    /// <see cref="IEnumerable{T}"/> path hands out a separate heap-safe cursor instead (one allocation, as LINQ expects).
    /// </summary>
    public ref struct Enumerator : IEnumerator<DagNodeView<N, A, V>>
    {
        private ImmMap<N, DagNode<N, A, V>>.Enumerator _e;   // span-based CHAMP walk over the node map

        internal Enumerator(DagHead<N, A, V>? h) => _e = (h?.Nodes ?? default).GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _e.MoveNext();

        public readonly DagNodeView<N, A, V> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ref readonly var n = ref _e.CurrentValue;
                return new(_e.CurrentKey, n.Alt, n.Value, n.Out, n.In);
            }
        }

        readonly object? IEnumerator.Current => Current;
        public void Reset() => _e.Reset();
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Non-escapable batch handle (see <see cref="ImmVector{T}.Scope"/>). The change-log spans (<see cref="AddedEdges"/>
    /// etc.) view live buffers: read them, but do not hold one across a further edit in the same batch.
    /// </summary>
    public readonly ref struct Scope : IDagAdjacency<N>
    {
        private readonly Transient _t;
        internal Scope(Transient t) => _t = t;

        public int NodeCount => _t.NodeCount;
        public int EdgeCount => _t.EdgeCount;
        public bool ContainsNode(N id) => _t.ContainsNode(id);
        public bool ContainsEdge(N from, N to) => _t.ContainsEdge(from, to);
        public bool TryGetAlternateKey(N id, [MaybeNullWhen(false)] out A alternateKey) => _t.TryGetAlternateKey(id, out alternateKey);
        public bool TryGetNodeByAlternateKey(A alternateKey, [MaybeNullWhen(false)] out N id) => _t.TryGetNodeByAlternateKey(alternateKey, out id);
        public bool TryGetValue(N id, [MaybeNullWhen(false)] out V value) => _t.TryGetValue(id, out value);
        public EdgeSet<N> Successors(N id) => _t.Successors(id);
        public EdgeSet<N> Predecessors(N id) => _t.Predecessors(id);
        Adjacency<N> IDagAdjacency<N>.Successors(N node) => new(_t.Successors(node));
        Adjacency<N> IDagAdjacency<N>.Predecessors(N node) => new(_t.Predecessors(node));
        public bool AddNode(N id, A alternateKey, V value) => _t.AddNode(id, alternateKey, value);
        public bool AddNode(N id, A alternateKey) => _t.AddNode(id, alternateKey, default!);
        public bool RemoveNode(N id) => _t.RemoveNode(id);
        public bool SetValue(N id, V value) => _t.SetValue(id, value);
        public bool ReplaceNode(N oldId, N newId) => _t.ReplaceNode(oldId, newId);
        public bool ReplaceNode(N oldId, N newId, A alternateKey, V value) => _t.ReplaceNode(oldId, newId, alternateKey, value);
        public bool AddEdge(N from, N to) => _t.AddEdge(from, to);
        public bool RemoveEdge(N from, N to) => _t.RemoveEdge(from, to);
        /// <inheritdoc cref="Transient.AddedEdges"/>
        public ReadOnlySpan<(N From, N To)> AddedEdges => _t.AddedEdges;
        /// <inheritdoc cref="Transient.RemovedEdges"/>
        public ReadOnlySpan<(N From, N To)> RemovedEdges => _t.RemovedEdges;
        /// <inheritdoc cref="Transient.AddedNodes"/>
        public ReadOnlySpan<N> AddedNodes => _t.AddedNodes;
        /// <inheritdoc cref="Transient.RemovedNodes"/>
        public ReadOnlySpan<N> RemovedNodes => _t.RemovedNodes;
        /// <inheritdoc cref="Transient.ReplacedNodes"/>
        public ReadOnlySpan<(N From, N To)> ReplacedNodes => _t.ReplacedNodes;
        /// <inheritdoc cref="Transient.UpdatedNodes"/>
        public ReadOnlySpan<N> UpdatedNodes => _t.UpdatedNodes;
        /// <inheritdoc cref="Transient.Origin"/>
        public ImmDag<N, A, V> Origin => _t.Origin;
    }

    /// <summary>
    /// Mutable view. Edits are in place on owned map and edge-set nodes; acyclicity is deferred to
    /// <see cref="ToPersistent()"/> / <see cref="Validate(out N[])"/>. A net change log relative to the start of the batch
    /// (<see cref="AddedEdges"/>, <see cref="RemovedEdges"/>, <see cref="AddedNodes"/>, <see cref="RemovedNodes"/>)
    /// lets incremental consumers (topological order, caches) process exactly what changed: an edge added then
    /// removed in the same batch appears in neither list. The spans view live buffers — do not hold one across a
    /// further edit. Not thread-safe.
    /// </summary>
    public sealed class Transient : IDagAdjacency<N>
    {
        private readonly ImmMap<N, DagNode<N, A, V>>.Transient _nodes = new();
        private readonly ImmMap<A, N>.Transient _alt = new();
        private int _edges;
        private N[]? _selfLoop;                              // the batch's first self-loop: never stored, it makes the batch cyclic
        private DagHead<N, A, V>? _last;
        private DagHead<N, A, V>? _origin;

        // net change log since batch start
        private readonly ChangeLog<(N, N)> _addedEdges = new(), _removedEdges = new();
        private readonly ChangeLog<N> _addedNodes = new(), _removedNodes = new();
        private readonly ChangeLog<(N, N)> _replaced = new();
        private readonly Dictionary<N, N> _renamedFrom = []; // current id -> id at batch start
        private readonly HashSet<N> _touchedSet = [];        // ids present at batch start whose entry was rewritten
        private readonly List<N> _touched = [], _updated = [];
        private bool _track = true;                          // record the change log
        private bool _seed = true;                           // untracked: still note added edges' targets, for the cycle check
        private readonly List<N> _seeds = [];

        // in-place edits of large edge sets (CHAMP): one owner per batch, spare capacity trimmed at freeze
        private readonly List<ChampNode<SetEntry<N>>> _setSlack = [];
        private ChampOwner<SetEntry<N>>? _setOwner;
        private ChampOwner<SetEntry<N>> SetOwner => _setOwner ??= new(_setSlack);

        // DFS scratch, reused across validations
        private readonly Dictionary<N, bool> _onPath = []; // true = on current DFS path (grey), false = done (black)
        private readonly List<(N Node, EdgeSet<N>.Cursor Next)> _stack = [];
        private SparseUse _onPathUse;

        [ThreadStatic] private static Transient? t_cache;

        internal static Transient Rent(DagHead<N, A, V>? head, DagBatchOptions options)
        {
            var t = t_cache;
            if (t is null) t = new Transient();
            else t_cache = null;
            t.Reset(new(head));
            t._track = !options.SkipChangeLog;
            t._seed = !options.SkipCycleCheck;
            return t;
        }

        internal static void Return(Transient t)
        {
            t.Clear();
            t.TrimScratch();
            t._track = true;
            t._seed = true;
            t_cache = t;
        }

        internal static Transient? Pooled => t_cache;                       // tests
        internal int ScratchCapacity => Math.Max(Math.Max(_onPath.Capacity, _touchedSet.Capacity), _renamedFrom.Capacity);
        internal int SearchCapacity => _onPath.Capacity;                    // tests

        // Pooled: bound what a large batch leaves behind, so that small batches clear little (see Scratch).
        private void TrimScratch()
        {
            _addedEdges.TrimExcess();
            _removedEdges.TrimExcess();
            _addedNodes.TrimExcess();
            _removedNodes.TrimExcess();
            _replaced.TrimExcess();
            Scratch.Trim(_renamedFrom);
            Scratch.Trim(_touchedSet);
            Scratch.Trim(_touched);
            Scratch.Trim(_updated);
            Scratch.Trim(_seeds);
            Scratch.Trim(_setSlack);
            Scratch.Trim(_onPath);
            Scratch.Trim(_stack);
        }

        /// <summary>
        /// Whether this transient records its change log (default true). Off, the log's accessors
        /// (<see cref="AddedEdges"/> and the rest) throw, and the batch saves the bookkeeping; validation still works.
        /// Switch it only between batches (after a <see cref="Reset"/> or a freeze).
        /// </summary>
        public bool TrackChanges
        {
            get => _track;
            set
            {
                if (value == _track) return;
                if (_addedEdges.Count + _removedEdges.Count + _addedNodes.Count + _removedNodes.Count + _replaced.Count
                    + _touched.Count + _seeds.Count != 0)
                    throw new InvalidOperationException("Change tracking can be switched only between batches.");
                _track = value;
            }
        }

        private void ThrowIfUntracked()
        {
            if (!_track) Throw.Untracked();
        }

        public int NodeCount => _nodes.Count;
        public int EdgeCount => _edges;
        /// <summary>A batch handle over this transient (e.g. to drive code written against <see cref="ImmDag{N, A, V}.Scope"/>).</summary>
        public Scope Scope => new(this);

        /// <summary>The version this batch started from (the last <see cref="Reset"/> or successful freeze). Nodes in
        /// <see cref="RemovedNodes"/> can still be read there, with their alternate key and payload.</summary>
        public ImmDag<N, A, V> Origin => new(_origin);

        /// <summary>Renames in this batch as (id at batch start, id now); chains collapse (a→b→c reports a→c) and a
        /// rename back cancels. A renamed node's old id is also in <see cref="RemovedNodes"/>, so consumers that treat
        /// removal specially should exclude these. Its new id is in <see cref="AddedNodes"/> unless that id belonged to
        /// a node present at the start of the batch (removed meanwhile, its id reused): then it is in neither list.</summary>
        public ReadOnlySpan<(N From, N To)> ReplacedNodes
        {
            get
            {
                ThrowIfUntracked();
                return _replaced.Items;
            }
        }

        /// <summary>Edges present now that were absent at the start of the batch, in the order they were added.</summary>
        public ReadOnlySpan<(N From, N To)> AddedEdges
        {
            get
            {
                ThrowIfUntracked();
                return _addedEdges.Items;
            }
        }

        /// <summary>Edges present at the start of the batch that are now gone (incl. those of removed / renamed nodes).</summary>
        public ReadOnlySpan<(N From, N To)> RemovedEdges
        {
            get
            {
                ThrowIfUntracked();
                return _removedEdges.Items;
            }
        }

        /// <summary>Nodes present now that were absent at the start of the batch (incl. renamed-to ids).</summary>
        public ReadOnlySpan<N> AddedNodes
        {
            get
            {
                ThrowIfUntracked();
                return _addedNodes.Items;
            }
        }

        /// <summary>Nodes present at the start of the batch that are now gone (incl. renamed-from ids).</summary>
        public ReadOnlySpan<N> RemovedNodes
        {
            get
            {
                ThrowIfUntracked();
                return _removedNodes.Items;
            }
        }

        /// <summary>Nodes present both at the start of the batch and now whose payload or alternate key differs
        /// (<c>EqualityComparer.Default</c>): set by <see cref="SetValue"/> or <see cref="ReplaceNode(N, N, A, V)"/>, removed
        /// and added again, or an id another node was renamed onto. In neither <see cref="AddedNodes"/> nor
        /// <see cref="RemovedNodes"/>. Computed when read, over the ids the batch rewrote.</summary>
        public ReadOnlySpan<N> UpdatedNodes
        {
            get
            {
                ThrowIfUntracked();
                _updated.Clear();
                if (_origin is not null)
                {
                    foreach (var id in _touched)
                    {
                        ref readonly var now = ref _nodes.GetValueRefOrNullRef(id);
                        if (Unsafe.IsNullRef(in now) || !_origin.Nodes.TryGetValue(id, out var was)) continue;
                        if (!EqualityComparer<V>.Default.Equals(was.Value, now.Value) || !EqualityComparer<A>.Default.Equals(was.Alt, now.Alt))
                            _updated.Add(id);
                    }
                }
                return CollectionsMarshal.AsSpan(_updated);
            }
        }

        /// <summary>Notes an id whose entry this batch rewrote (see <see cref="UpdatedNodes"/>, which reads the origin
        /// only then: no lookup here).</summary>
        private void Touched(N id)
        {
            if (_track && _touchedSet.Add(id)) _touched.Add(id);
        }

        public bool ContainsNode(N id) => _nodes.ContainsKey(id);

        public bool ContainsEdge(N from, N to)
        {
            ref readonly var e = ref _nodes.GetValueRefOrNullRef(from);
            return !Unsafe.IsNullRef(in e) && e.Out.Contains(to);
        }

        public bool TryGetAlternateKey(N id, [MaybeNullWhen(false)] out A alternateKey)
        {
            ref readonly var e = ref _nodes.GetValueRefOrNullRef(id);
            if (Unsafe.IsNullRef(in e)) { alternateKey = default; return false; }
            alternateKey = e.Alt;
            return true;
        }

        public bool TryGetNodeByAlternateKey(A alternateKey, [MaybeNullWhen(false)] out N id) => _alt.TryGetValue(alternateKey, out id);

        public bool TryGetValue(N id, [MaybeNullWhen(false)] out V value)
        {
            ref readonly var e = ref _nodes.GetValueRefOrNullRef(id);
            if (Unsafe.IsNullRef(in e)) { value = default; return false; }
            value = e.Value;
            return true;
        }

        public EdgeSet<N> Successors(N id) => NodeRef(id).Out;
        public EdgeSet<N> Predecessors(N id) => NodeRef(id).In;
        Adjacency<N> IDagAdjacency<N>.Successors(N node) => new(NodeRef(node).Out);
        Adjacency<N> IDagAdjacency<N>.Predecessors(N node) => new(NodeRef(node).In);

        private ref readonly DagNode<N, A, V> NodeRef(N id)
        {
            ref readonly var e = ref _nodes.GetValueRefOrNullRef(id);
            if (Unsafe.IsNullRef(in e)) Throw.KeyNotFound(id);
            return ref e;
        }

        private void EdgeAdded(N from, N to)
        {
            if (!_track) { if (_seed) _seeds.Add(to); return; }
            if (!_removedEdges.Remove((from, to))) _addedEdges.Add((from, to));
        }

        private void EdgeRemoved(N from, N to)
        {
            if (_track && !_addedEdges.Remove((from, to))) _removedEdges.Add((from, to));
        }
        /// <summary>Logs an added node; true if it cancels a removal (the id was present at the start of the batch).</summary>
        private bool NodeAdded(N id)
        {
            if (!_track) return false;
            if (_removedNodes.Remove(id)) return true;
            _addedNodes.Add(id);
            return false;
        }
        private void NodeRemoved(N id) { if (_track && !_addedNodes.Remove(id)) _removedNodes.Add(id); }

        private void LogRename(N oldId, N newId)
        {
            if (!_track) return;
            N original;
            if (_renamedFrom.Remove(oldId, out var o)) { _replaced.Remove((o, oldId)); original = o; }
            else if (_origin is not null && _origin.Nodes.ContainsKey(oldId)) original = oldId;
            else return; // renaming a node created in this batch: nothing existed at the start to rename
            if (EqualityComparer<N>.Default.Equals(original, newId)) return; // renamed back
            _renamedFrom[newId] = original;
            _replaced.Add((original, newId));
        }

        /// <summary>Adds a node; false if the id or the alternate key is already present.</summary>
        public bool AddNode(N id, A alternateKey, V value)
        {
            // Read-only checks first: measured as fast as insert-and-undo, long or string keys (the reads warm the paths the
            // inserts walk), so a caller who checked first loses nothing.
            if (_nodes.ContainsKey(id) || _alt.ContainsKey(alternateKey)) return false;
            _last = null;
            ref var e = ref _nodes.GetValueRefOrAddDefault(id, out _);
            e.Alt = alternateKey;
            e.Value = value;
            _alt.GetValueRefOrAddDefault(alternateKey, out _) = id;
            if (NodeAdded(id)) Touched(id);                  // removed and added again within the batch
            return true;
        }

        public bool AddNode(N id, A alternateKey) => AddNode(id, alternateKey, default!);

        /// <summary>Removes a node and all its incident edges (found via the predecessor index, no scan).</summary>
        public bool RemoveNode(N id)
        {
            if (!_nodes.TryGetValue(id, out var e)) return false;
            _last = null;
            var o = SetOwner;
            foreach (var s in e.Out)
            {
                ref var se = ref _nodes.GetValueRefOrAddDefault(s, out _);
                se.In = se.In.Remove(id, o, out _);
                EdgeRemoved(id, s);
            }
            foreach (var p in e.In)
            {
                ref var pe = ref _nodes.GetValueRefOrAddDefault(p, out _);
                pe.Out = pe.Out.Remove(id, o, out _);
                EdgeRemoved(p, id);
            }
            _edges -= e.Out.Count + e.In.Count;
            _nodes.Remove(id);
            _alt.Remove(e.Alt);
            if (_track && _renamedFrom.Remove(id, out var original)) _replaced.Remove((original, id)); // the original is now removed
            NodeRemoved(id);
            return true;
        }

        /// <summary>Replaces a node's payload, keeping everything else; false if absent.</summary>
        public bool SetValue(N id, V value)
        {
            if (!_nodes.ContainsKey(id)) return false;
            _nodes.GetValueRefOrAddDefault(id, out _).Value = value;
            _last = null;
            Touched(id);
            return true;
        }

        /// <summary>Renames a node keeping its alternate key, payload and edges.</summary>
        public bool ReplaceNode(N oldId, N newId)
        {
            ref readonly var e = ref _nodes.GetValueRefOrNullRef(oldId);
            return !Unsafe.IsNullRef(in e) && ReplaceNode(oldId, newId, e.Alt, e.Value);
        }

        /// <summary>
        /// Gives node <paramref name="oldId"/> a new id, alternate key and payload, keeping every edge: each
        /// neighbour's adjacency is re-pointed (O(degree)). False if <paramref name="oldId"/> is
        /// absent, or <paramref name="newId"/> / <paramref name="alternateKey"/> belong to another node. In the change
        /// log a rename shows as old id removed / new id added with the incident edges moved accordingly.
        /// </summary>
        public bool ReplaceNode(N oldId, N newId, A alternateKey, V value)
        {
            if (!_nodes.TryGetValue(oldId, out var e)) return false;
            var sameId = EqualityComparer<N>.Default.Equals(oldId, newId);
            var sameAlt = EqualityComparer<A>.Default.Equals(e.Alt, alternateKey);
            if (!sameId && _nodes.ContainsKey(newId)) return false;
            if (!sameAlt && _alt.ContainsKey(alternateKey)) return false;
            _last = null;

            if (sameId)
            {
                ref var x = ref _nodes.GetValueRefOrAddDefault(oldId, out _);
                x.Value = value;
                if (!sameAlt)
                {
                    _alt.Remove(e.Alt);
                    _alt[alternateKey] = oldId;
                    x.Alt = alternateKey;
                }
                Touched(oldId);
                return true;
            }

            var o = SetOwner;
            foreach (var s in e.Out)
            {
                ref var se = ref _nodes.GetValueRefOrAddDefault(s, out _);
                se.In = se.In.Remove(oldId, o, out _).Add(newId, o, out _);
                EdgeRemoved(oldId, s);
                EdgeAdded(newId, s);
            }
            foreach (var p in e.In)
            {
                ref var pe = ref _nodes.GetValueRefOrAddDefault(p, out _);
                pe.Out = pe.Out.Remove(oldId, o, out _).Add(newId, o, out _);
                EdgeRemoved(p, oldId);
                EdgeAdded(p, newId);
            }
            _nodes.Remove(oldId);
            _nodes.GetValueRefOrAddDefault(newId, out _) = new DagNode<N, A, V> { Alt = alternateKey, Value = value, Out = e.Out, In = e.In };
            _alt.Remove(e.Alt);
            _alt[alternateKey] = newId;
            LogRename(oldId, newId);
            NodeRemoved(oldId);
            if (NodeAdded(newId)) Touched(newId);            // renamed onto an id present at the start
            return true;
        }

        /// <summary>
        /// Adds an edge (both nodes must exist); false if already present (nothing is touched, so a batch of no-ops
        /// returns the identical version). Cycles are reported at the end of the batch, a self-loop too: it is never
        /// stored (<see cref="ContainsEdge"/> stays false), but the batch stays cyclic until <see cref="Reset"/>.
        /// Three map traversals: read the source, edit the target, edit the source.
        /// </summary>
        public bool AddEdge(N from, N to)
        {
            if (EqualityComparer<N>.Default.Equals(from, to))
            {
                if (!_nodes.ContainsKey(from)) Throw.KeyNotFound(from);
                _selfLoop ??= [from, to];
                return true;
            }
            ref readonly var fr = ref _nodes.GetValueRefOrNullRef(from);
            if (Unsafe.IsNullRef(in fr)) Throw.KeyNotFound(from);
            if (fr.Out.Contains(to)) return false;

            var o = SetOwner;
            ref var te = ref _nodes.GetValueRefOrAddDefault(to, out var toExists);
            if (!toExists)
            {
                _nodes.Remove(to); // undo the probe insert
                Throw.KeyNotFound(to);
            }
            te.In = te.In.Add(from, o, out _);
            ref var fe = ref _nodes.GetValueRefOrAddDefault(from, out _); // separate lookup: never hold two refs at once
            fe.Out = fe.Out.Add(to, o, out _);
            _edges++;
            EdgeAdded(from, to);
            _last = null;
            return true;
        }

        /// <summary>
        /// Adds an edge after proving it closes no cycle, by a search from <paramref name="to"/> for
        /// <paramref name="from"/> before any edit (single edits: a batch checks once, at its end). On a cycle returns
        /// false with it, and nothing changes; present already, true (nothing changes either).
        /// </summary>
        internal bool TryAddEdgeAcyclic(N from, N to, [NotNullWhen(false)] out N[]? cycle)
        {
            cycle = null;
            ref readonly var fr = ref NodeRef(from);
            NodeRef(to);
            if (EqualityComparer<N>.Default.Equals(from, to)) { cycle = [from, to]; return false; }
            if (fr.Out.Contains(to)) return true;
            if (Reaches(to, from, out cycle)) return false;
            AddEdge(from, to);
            return true;
        }

        /// <summary>Depth-first from <paramref name="start"/> for <paramref name="target"/>; reaching it gives the cycle an
        /// edge target → start would close (target, start, …, target).</summary>
        private bool Reaches(N start, N target, [NotNullWhen(true)] out N[]? cycle)
        {
            cycle = null;
            try
            {
                _onPath[start] = true;
                _stack.Add((start, NodeRef(start).Out.GetCursor()));
                while (_stack.Count > 0)
                {
                    var top = _stack.Count - 1;
                    var frame = _stack[top];
                    if (!frame.Next.MoveNext())
                    {
                        _stack.RemoveAt(top);
                        continue;
                    }
                    var w = frame.Next.Current;
                    _stack[top] = frame; // persist the advanced enumerator
                    if (EqualityComparer<N>.Default.Equals(w, target))
                    {
                        cycle = new N[top + 3];
                        cycle[0] = target;
                        for (var k = 0; k <= top; k++) cycle[k + 1] = _stack[k].Node;
                        cycle[^1] = target;
                        return true;
                    }
                    if (_onPath.TryAdd(w, true)) _stack.Add((w, NodeRef(w).Out.GetCursor()));
                }
                return false;
            }
            finally
            {
                _stack.Clear();
                _onPathUse.Clear(_onPath);
            }
        }

        public bool RemoveEdge(N from, N to)
        {
            if (!ContainsEdge(from, to)) return false;
            var o = SetOwner;
            ref var fe = ref _nodes.GetValueRefOrAddDefault(from, out _);
            fe.Out = fe.Out.Remove(to, o, out _);
            ref var te = ref _nodes.GetValueRefOrAddDefault(to, out _);
            te.In = te.In.Remove(from, o, out _);
            _edges--;
            EdgeRemoved(from, to);
            _last = null;
            return true;
        }

        /// <summary>
        /// Checks acyclicity. Any new cycle must contain an edge added in this batch, hence lies within the
        /// sub-graph reachable from those edges' targets: iterative three-colour DFS over just that region,
        /// O(reachable nodes + edges). Returns one cycle (first == last) if found.
        /// </summary>
        public bool Validate([NotNullWhen(false)] out N[]? cycle)
        {
            if (ValidateCore(wantCycle: true, out var c)) { cycle = null; return true; }
            cycle = c!;
            return false;
        }

        /// <summary>As <see cref="Validate(out N[])"/>, without the cycle: a refusal allocates nothing.</summary>
        public bool Validate() => ValidateCore(wantCycle: false, out _);

        private bool ValidateCore(bool wantCycle, out N[]? cycle)
        {
            cycle = null;
            if (_selfLoop is not null) { if (wantCycle) cycle = _selfLoop; return false; }
            if (!_track && !_seed) throw new InvalidOperationException("This batch skipped its cycle check: it cannot be validated.");
            var added = _track ? _addedEdges.Items : default;
            var seeds = _track ? default : CollectionsMarshal.AsSpan(_seeds);
            var count = _track ? added.Length : seeds.Length;
            if (count == 0) return true;
            _onPath.Clear();
            try
            {
                for (var si = 0; si < count; si++)
                {
                    var seed = _track ? added[si].Item2 : seeds[si];
                    if (!_onPath.TryAdd(seed, true)) continue; // already explored
                    _stack.Add((seed, NodeRef(seed).Out.GetCursor()));
                    while (_stack.Count > 0)
                    {
                        var top = _stack.Count - 1;
                        var frame = _stack[top];
                        if (!frame.Next.MoveNext())
                        {
                            _onPath[frame.Node] = false;
                            _stack.RemoveAt(top);
                            continue;
                        }
                        var w = frame.Next.Current;
                        _stack[top] = frame; // persist the advanced enumerator
                        ref var grey = ref CollectionsMarshal.GetValueRefOrAddDefault(_onPath, w, out var seen);
                        if (seen)
                        {
                            if (!grey) continue;
                            if (!wantCycle) return false;
                            var i = top;
                            while (!EqualityComparer<N>.Default.Equals(_stack[i].Node, w)) i--;
                            cycle = new N[top - i + 2];
                            for (var k = i; k <= top; k++) cycle[k - i] = _stack[k].Node;
                            cycle[^1] = w;
                            return false;
                        }
                        grey = true;
                        _stack.Add((w, NodeRef(w).Out.GetCursor()));
                    }
                }
                return true;
            }
            finally
            {
                _stack.Clear();
                _onPathUse.Clear(_onPath);
            }
        }

        /// <summary>Validates and freezes; throws <see cref="DagCycleException{N}"/> on a cycle (state is kept, so
        /// the caller may inspect, fix or <see cref="Reset"/>). Starts a new change log.</summary>
        public ImmDag<N, A, V> ToPersistent() => ToPersistent(skipCycleCheck: false);

        /// <summary>As <see cref="ToPersistent()"/>; with <paramref name="skipCycleCheck"/>, freezes without validating
        /// (see <see cref="DagBatchOptions.SkipCycleCheck"/>).</summary>
        public ImmDag<N, A, V> ToPersistent(bool skipCycleCheck)
        {
            if (skipCycleCheck)
            {
                if (_selfLoop is not null) Throw.Cycle(_selfLoop);
                return Freeze();
            }
            if (!TryToPersistent(out var r, out var cycle)) Throw.Cycle(cycle);
            return r;
        }

        public bool TryToPersistent(out ImmDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
        {
            if (!Validate(out cycle)) { result = default; return false; }
            result = Freeze();
            return true;
        }

        /// <summary>As <see cref="TryToPersistent(out ImmDag{N, A, V}, out N[])"/>, without the cycle: a refusal allocates
        /// nothing.</summary>
        public bool TryToPersistent(out ImmDag<N, A, V> result)
        {
            if (!ValidateCore(wantCycle: false, out _)) { result = default; return false; }
            result = Freeze();
            return true;
        }

        /// <summary>The batch's first self-loop (first == last), if it added one.</summary>
        internal N[]? SelfLoop => _selfLoop;

        private ImmDag<N, A, V> Freeze()
        {
            ClearLog();
            if (_setSlack.Count != 0) Champ<SetEntry<N>, N, DefaultHasher<N>>.Trim(_setSlack);
            _setOwner = null; // freeze every edge-set node created so far
            var nodes = _nodes.ToPersistent();
            var alt = _alt.ToPersistent();
            var l = _last;
            if (l is null || !l.Nodes.IsSameAs(nodes) || !l.ByAlt.IsSameAs(alt))
                _last = new DagHead<N, A, V> { Nodes = nodes, ByAlt = alt, Edges = _edges };
            _origin = _last;
            return new(_last);
        }

        private void ClearLog()
        {
            _addedEdges.Clear();
            _removedEdges.Clear();
            _addedNodes.Clear();
            _removedNodes.Clear();
            _replaced.Clear();
            _renamedFrom.Clear();
            _seeds.Clear();
            _touchedSet.Clear();
            _touched.Clear();
            _updated.Clear();
        }

        public void Reset(ImmDag<N, A, V> source)
        {
            var h = source._h;
            _nodes.Reset(h?.Nodes ?? default);
            _alt.Reset(h?.ByAlt ?? default);
            _edges = h?.Edges ?? 0;
            _selfLoop = null;
            ClearLog();
            _setSlack.Clear();
            _setOwner = null;
            _last = h;
            _origin = h;
        }

        public void Clear() => Reset(default);
    }
}

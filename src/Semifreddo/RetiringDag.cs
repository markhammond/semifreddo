using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Semifreddo.Reclamation;

/// <summary>A payload that left the graph and awaits reclamation: the node it belonged to (its id and alternate key in
/// the version it was retired from) and the retirement's <paramref name="Stamp"/>, unique and never reused.</summary>
public readonly record struct RetiredNode<N, A, V>(N Id, A AlternateKey, V Value, long Stamp) where N : notnull;

internal sealed class RetiringHead<N, A, V> where N : notnull where A : notnull
{
    public ImmDag<N, A, V> Graph;
    public ImmMap<long, RetiredNode<N, A, V>> Retired;   // by stamp
}

/// <summary>
/// An <see cref="ImmDag{N, A, V}"/> plus a retirement ledger for deferred reclamation of payloads that leave the graph
/// (the retire / reclaim lifecycle of safe memory reclamation). Payloads are compared with
/// <c>EqualityComparer&lt;V&gt;.Default</c>: reference equality for classes without overrides.
/// <list type="bullet">
/// <item>At each commit, every payload the batch's origin version held that the graph no longer holds is retired, with
/// the id and alternate key it had there — whether its node was removed, given a new payload (<c>SetValue</c>,
/// <c>ReplaceNode</c>, removed and added again) or had another node renamed onto its id. A payload that only moved (a
/// rename) is not retired, and a node added and removed within one batch was never published, so it is never
/// retired.</item>
/// <item>A payload that comes back into the graph (an undo, under any id) is live again: its pending retirement is
/// dropped. A batch that adds or updates nodes therefore scans the ledger: keep it reclaimed.</item>
/// <item>Reclaim protocol: read <see cref="Retired"/> from version Vk (the candidates); wait until every holder of a
/// version older than Vk is done (evaluations, snapshots, diagnostics); reclaim the candidates against the current
/// value with <see cref="RetiringDag.Reclaim{N, A, V}"/>; then release only the payloads it reports reclaimed. A
/// candidate is matched by its stamp, so one whose payload came back meanwhile (even the same payload, retired again
/// later under a new stamp) is not reclaimed, and is not released.</item>
/// </list>
/// Single-reference readonly struct (tear-free, CAS-able).
/// </summary>
public readonly struct RetiringDag<N, A, V> where N : notnull where A : notnull
{
    private static long s_stamp;   // retirement stamps: unique within this instantiation, never reused
    private readonly RetiringHead<N, A, V>? _h;

    internal RetiringDag(RetiringHead<N, A, V>? h) => _h = h is null || (h.Graph.IsEmpty && h.Retired.IsEmpty) ? null : h;

    internal RetiringHead<N, A, V>? Head => _h;

    public static RetiringDag<N, A, V> Empty => default;

    /// <summary>The current graph.</summary>
    public ImmDag<N, A, V> Graph => _h?.Graph ?? default;

    public bool IsSameAs(RetiringDag<N, A, V> other) => ReferenceEquals(_h, other._h);

    // ------------------------------------------------------------------------------------------------ ledger

    public int RetiredCount => _h is null ? 0 : _h.Retired.Count;
    public bool HasRetired => RetiredCount != 0;

    /// <summary>The retirement with <paramref name="stamp"/>, if it is still pending.</summary>
    public bool TryGetRetired(long stamp, out RetiredNode<N, A, V> node)
    {
        if (_h is not null) return _h.Retired.TryGetValue(stamp, out node);
        node = default;
        return false;
    }

    /// <summary>Pending retirements (hash order).</summary>
    public IEnumerable<RetiredNode<N, A, V>> Retired => (_h?.Retired ?? default).Values;

    /// <summary>Copies the pending retirements into <paramref name="destination"/> (length &gt;= RetiredCount);
    /// allocation-free.</summary>
    public int CopyRetiredTo(Span<RetiredNode<N, A, V>> destination)
    {
        var i = 0;
        if (_h is null) return 0;
        foreach (var kv in _h.Retired) destination[i++] = kv.Value;
        return i;
    }

    /// <summary>Reclaims each candidate still pending (matched by stamp). See <see cref="RetiringDag.Reclaim{N, A, V}"/>
    /// for the compare-and-swap form that reports what it reclaimed.</summary>
    public RetiringDag<N, A, V> Reclaim(ReadOnlySpan<RetiredNode<N, A, V>> candidates) => ReclaimCore(candidates, null);

    /// <summary>Reclaims each candidate still pending (matched by stamp), adding each to <paramref name="reclaimed"/>.</summary>
    public RetiringDag<N, A, V> Reclaim(ReadOnlySpan<RetiredNode<N, A, V>> candidates, List<RetiredNode<N, A, V>> reclaimed)
    {
        ArgumentNullException.ThrowIfNull(reclaimed);
        return ReclaimCore(candidates, reclaimed);
    }

    private RetiringDag<N, A, V> ReclaimCore(ReadOnlySpan<RetiredNode<N, A, V>> candidates, List<RetiredNode<N, A, V>>? reclaimed)
    {
        if (_h is null || _h.Retired.IsEmpty || candidates.IsEmpty) return this;
        var retired = _h.Retired;
        var changed = false;
        foreach (var c in candidates)
        {
            if (!retired.TryGetValue(c.Stamp, out var r)) continue;
            retired = retired.Remove(c.Stamp);
            reclaimed?.Add(r);
            changed = true;
        }
        return changed ? With(_h.Graph, retired) : this;
    }

    /// <summary>Drops every pending retirement (for a caller that knows no reader can hold them).</summary>
    public RetiringDag<N, A, V> ReclaimAll() => HasRetired ? With(_h!.Graph, default) : this;

    private RetiringDag<N, A, V> With(ImmDag<N, A, V> graph, ImmMap<long, RetiredNode<N, A, V>> retired)
        => new(new RetiringHead<N, A, V> { Graph = graph, Retired = retired });

    // ------------------------------------------------------------------------------------------------ batches

    [ThreadStatic] private static ImmDag<N, A, V>.Transient? t_graph;
    [ThreadStatic] private static ImmMap<long, RetiredNode<N, A, V>>.Transient? t_retired;
    [ThreadStatic] private static Scratch? t_scratch;

    /// <summary>Applies a batch to the graph (acyclicity validated, as <see cref="ImmDag{N, A, V}.Mutate{TArg}(TArg, Action{ImmDag{N, A, V}.Scope, TArg})"/>) and
    /// updates the ledger. On a cycle nothing changes and <see cref="DagCycleException{N}"/> is thrown.</summary>
    public RetiringDag<N, A, V> Mutate<TArg>(TArg arg, Action<ImmDag<N, A, V>.Scope, TArg> batch) where TArg : allows ref struct
        => Apply(arg, batch, validate: true, out var r, out _) ? r : throw new UnreachableException();

    public RetiringDag<N, A, V> Mutate(Action<ImmDag<N, A, V>.Scope> batch)
        => Mutate(batch, static (s, b) => b(s));

    /// <summary>As <see cref="Mutate{TArg}(TArg, Action{ImmDag{N, A, V}.Scope, TArg})"/>, with <paramref name="options"/>:
    /// <see cref="DagBatchOptions.SkipCycleCheck"/> for a caller that guarantees acyclicity. The ledger is built from the
    /// change log, so <see cref="DagBatchOptions.SkipChangeLog"/> is refused.</summary>
    /// <exception cref="ArgumentException"><paramref name="options"/> skips the change log.</exception>
    public RetiringDag<N, A, V> Mutate<TArg>(TArg arg, Action<ImmDag<N, A, V>.Scope, TArg> batch, DagBatchOptions options) where TArg : allows ref struct
    {
        if (options.SkipChangeLog) throw new ArgumentException("The retirement ledger is built from the change log: it cannot be skipped.", nameof(options));
        return Apply(arg, batch, validate: !options.SkipCycleCheck, out var r, out _) ? r : throw new UnreachableException();
    }

    /// <summary>Non-throwing variant: on a cycle returns false with the cycle; nothing changes.</summary>
    public bool TryMutate<TArg>(TArg arg, Action<ImmDag<N, A, V>.Scope, TArg> batch, out RetiringDag<N, A, V> result, [NotNullWhen(false)] out N[]? cycle)
        where TArg : allows ref struct
    {
        try { return Apply(arg, batch, validate: true, out result, out cycle, throwOnCycle: false); }
        catch (DagCycleException<N> e) { result = this; cycle = e.Cycle; return false; }
    }

    public RetiringDag<N, A, V> AddNode(N id, A alternateKey, V value) => Mutate((id, alternateKey, value), static (s, a) =>
    {
        if (!s.AddNode(a.id, a.alternateKey, a.value)) throw new ArgumentException($"Node {a.id} or alternate key {a.alternateKey} already present.");
    });

    public RetiringDag<N, A, V> RemoveNode(N id) => Mutate(id, static (s, n) => s.RemoveNode(n));

    private bool Apply<TArg>(TArg arg, Action<ImmDag<N, A, V>.Scope, TArg> batch, bool validate,
        out RetiringDag<N, A, V> result, out N[]? cycle, bool throwOnCycle = true) where TArg : allows ref struct
    {
        var t = t_graph ?? new ImmDag<N, A, V>.Transient();
        var r = t_retired ?? new ImmMap<long, RetiredNode<N, A, V>>.Transient();
        var x = t_scratch ?? new Scratch();
        t_graph = null;
        t_retired = null;
        t_scratch = null;
        try
        {
            t.Reset(Graph);
            batch(t.Scope, arg);

            // ledger first (the freeze below clears the change log); committed only if the graph commits
            var pending = _h?.Retired ?? default;
            r.Reset(pending);
            Retire(t, pending, r, x);

            ImmDag<N, A, V> graph;
            if (!validate) graph = t.ToPersistent(skipCycleCheck: true);
            else if (!t.TryToPersistent(out graph, out cycle))
            {
                if (throwOnCycle) throw new DagCycleException<N>(cycle);
                result = this;
                return false;
            }
            cycle = null;
            var retired = r.ToPersistent();
            result = graph.IsSameAs(Graph) && retired.IsSameAs(_h?.Retired ?? default) ? this : With(graph, retired);
            return true;
        }
        finally
        {
            t.Clear();
            r.Clear();
            x.Clear();
            t_graph = t;
            t_retired = r;
            t_scratch = x;
        }
    }

    /// <summary>Retires the origin payloads the batch dropped, and drops the retirements of payloads it brought back.</summary>
    private static void Retire(ImmDag<N, A, V>.Transient t, ImmMap<long, RetiredNode<N, A, V>> pending,
        ImmMap<long, RetiredNode<N, A, V>>.Transient r, Scratch x)
    {
        var origin = t.Origin;
        var updated = t.UpdatedNodes;
        // what the graph holds now, among the nodes the batch added or rewrote (a multiset: payloads may be equal)
        foreach (var id in t.AddedNodes) if (t.TryGetValue(id, out var v)) x.Enter(v);
        foreach (var id in updated) if (t.TryGetValue(id, out var v)) x.Enter(v);
        // what the origin held among the nodes the batch removed or rewrote: retired unless it is still held
        foreach (var id in t.RemovedNodes) RetireIfGone(origin, id, r, x);
        foreach (var id in updated) RetireIfGone(origin, id, r, x);
        // a payload brought back while retired is live again (the retirements pending before this batch)
        if (x.Entered == 0 || pending.IsEmpty) return;
        foreach (var kv in pending)
            if (x.TryConsume(kv.Value.Value)) r.Remove(kv.Key);
    }

    private static void RetireIfGone(ImmDag<N, A, V> origin, N id, ImmMap<long, RetiredNode<N, A, V>>.Transient r, Scratch x)
    {
        if (!origin.TryGetNode(id, out var node) || x.TryConsume(node.Value)) return;   // still held (moved, kept)
        var stamp = Interlocked.Increment(ref s_stamp);
        r[stamp] = new RetiredNode<N, A, V>(id, node.AlternateKey, node.Value, stamp);
    }

    /// <summary>Per-thread scratch for one commit: the payloads entering the graph, as a multiset.</summary>
    private sealed class Scratch
    {
        private readonly Dictionary<Payload, int> _entering = [];
        public int Entered { get; private set; }

        public void Enter(V value)
        {
            var k = new Payload(value);
            _entering[k] = _entering.GetValueOrDefault(k) + 1;
            Entered++;
        }

        /// <summary>Takes one entering payload equal to <paramref name="value"/>, if any is left.</summary>
        public bool TryConsume(V value)
        {
            if (Entered == 0) return false;
            var k = new Payload(value);
            if (!_entering.TryGetValue(k, out var n)) return false;
            if (n == 1) _entering.Remove(k);
            else _entering[k] = n - 1;
            Entered--;
            return true;
        }

        public void Clear()
        {
            _entering.Clear();
            Entered = 0;
        }
    }

    /// <summary>A payload as a dictionary key, compared with <c>EqualityComparer&lt;V&gt;.Default</c> (null allowed).</summary>
    private readonly struct Payload(V value) : IEquatable<Payload>
    {
        private readonly V _value = value;
        public bool Equals(Payload other) => EqualityComparer<V>.Default.Equals(_value, other._value);
        public override bool Equals(object? obj) => obj is Payload p && Equals(p);
        public override int GetHashCode() => _value is null ? 0 : EqualityComparer<V>.Default.GetHashCode(_value);
    }
}

public static class RetiringDag
{
    /// <summary>Lock-free CAS update of a shared <see cref="RetiringDag{N, A, V}"/> (single reference).</summary>
    public static bool Update<N, A, V, TArg>(ref RetiringDag<N, A, V> location, Func<RetiringDag<N, A, V>, TArg, RetiringDag<N, A, V>> transformer, TArg arg)
        where N : notnull where A : notnull
    {
        ref var loc = ref Unsafe.As<RetiringDag<N, A, V>, RetiringHead<N, A, V>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    /// <summary>
    /// Reclaims <paramref name="candidates"/> from the shared <paramref name="location"/> (compare-and-swap, retried as
    /// needed) and fills <paramref name="reclaimed"/> with exactly those the committed swap reclaimed: the payloads that
    /// are now safe to release. Candidates no longer pending (reclaimed elsewhere, or brought back) are left out.
    /// Returns true if anything was reclaimed.
    /// </summary>
    public static bool Reclaim<N, A, V>(ref RetiringDag<N, A, V> location, ReadOnlySpan<RetiredNode<N, A, V>> candidates,
        List<RetiredNode<N, A, V>> reclaimed) where N : notnull where A : notnull
    {
        ArgumentNullException.ThrowIfNull(reclaimed);
        ref var loc = ref Unsafe.As<RetiringDag<N, A, V>, RetiringHead<N, A, V>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            reclaimed.Clear();                                   // only the attempt that commits reports
            var next = new RetiringDag<N, A, V>(cur).Reclaim(candidates, reclaimed).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }
}

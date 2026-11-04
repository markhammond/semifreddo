using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Semifreddo.Internal;

namespace Semifreddo;

internal sealed class TopoHead<N, TEntry> where N : notnull where TEntry : struct, ITopoEntry
{
    public ImmMap<N, TEntry> Rank;     // node -> rank (+ level); position in Slots = (int)(rank - Base)
    public ImmFlexVector<int> Widths;  // nodes per level, last entry non-zero (empty without levels)
    public ImmFlexVector<N> Slots;  // topological order; ends always live, interior holes hold the sentinel
    public uint Base;
    public int Dead;
}

/// <summary>
/// Persistent incremental topological order (Pearce &amp; Kelly 2006), maintained beside a DAG it does not own.
/// <list type="bullet">
/// <item>Ranks are positions in an RRB vector (in-order iteration is a span scan; holes from removals hold
/// <typeparamref name="TSentinel"/>'s value, are trimmed at the ends and compacted when they outnumber live nodes).</item>
/// <item>Ranks are stored as <c>uint</c> = Base + position with wrap-around arithmetic: the live window is bounded by
/// the vector's <c>int</c> count, so Base may wrap freely and nothing ever needs renumbering for overflow.</item>
/// <item><c>AddEdge</c>: O(1) when the edge already agrees with the order; otherwise PK's bounded forward/backward
/// search over the affected window, then reassignment of that window's positions. Cycles are reported, not thrown
/// internally.</item>
/// <item>Batches (<c>TryAddEdges</c>): few out-of-order edges run PK edge by edge (the batch's not-yet-processed
/// out-of-order edges are hidden from the searches, which keeps PK's precondition); many run one Kahn re-sort of the
/// union window [min pos(to), max pos(from)] — every new cycle lies inside it, so detection is complete.</item>
/// <item>Removing nodes or edges never invalidates an order: <c>Remove</c> only drops the node's slot.</item>
/// <item>Levels (parallel evaluation groups) are maintained too: raised along successors when an edge would put a node
/// at or below its predecessor's level, exact again after <c>Tighten</c>; <c>LevelWidth</c> gives each group's size,
/// <c>CopyByLevel</c> lays the nodes out group by group (topological order within a group), and
/// <c>ComputeBottomLevels</c> derives cost-weighted critical-path priorities for list scheduling.</item>
/// </list>
/// Every node of the graph passed to edge operations must be present in the order. Shared engine of the public
/// <see cref="ImmPkTopoOrder{N, TSentinel}"/> (<see cref="NoLevels"/>) and <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/>
/// (<see cref="WithLevels"/>): the entry policy is a compile-time constant, so the order-only variant carries no level
/// code or storage.
/// </summary>
internal readonly struct TopoCore<N, TSentinel, TEntry>
    where N : notnull
    where TSentinel : struct, INodeSentinel<N>
    where TEntry : struct, ITopoEntry
{
    private readonly TopoHead<N, TEntry>? _h;

    internal TopoHead<N, TEntry>? Head => _h;

    internal TopoCore(TopoHead<N, TEntry>? h) => _h = h is null || h.Rank.IsEmpty ? null : h;

    public static TopoCore<N, TSentinel, TEntry> Empty => default;
    public int Count => _h is null ? 0 : _h.Rank.Count;
    public bool IsEmpty => _h is null;
    public bool IsSameAs(TopoCore<N, TSentinel, TEntry> other) => ReferenceEquals(_h, other._h);
    public bool Contains(N node) => _h is not null && _h.Rank.ContainsKey(node);

    /// <summary>Position of <paramref name="node"/> in the order: comparable within the same version only.</summary>
    public bool TryGetRank(N node, out long rank)
    {
        if (_h is not null && _h.Rank.TryGetValue(node, out var e))
        {
            rank = (int)(e.Rank - _h.Base);
            return true;
        }
        rank = 0;
        return false;
    }

    internal bool TryGetLevelCore(N node, out int level)
    {
        if (_h is not null && _h.Rank.TryGetValue(node, out var e)) { level = e.Level; return true; }
        level = 0;
        return false;
    }

    internal int LevelCountCore => _h is null ? 0 : _h.Widths.Count;

    internal int LevelWidthCore(int level) => _h is not null && (uint)level < (uint)_h.Widths.Count ? _h.Widths[level] : 0;

    internal TopoCore<N, TSentinel, TEntry> TightenCore<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var t = Transient.Rent(_h);
        try { t.TightenCore(graph); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    /// <summary>
    /// Writes the nodes group by group into <paramref name="nodes"/> (length &gt;= Count), topological order within a
    /// group, and the start of each group into <paramref name="levelStarts"/> (length &gt;= LevelCount + 1, last entry =
    /// Count). Group k is <c>nodes[levelStarts[k]..levelStarts[k + 1]]</c>. Allocation-free.
    /// </summary>
    internal void CopyByLevelCore(Span<N> nodes, Span<int> levelStarts)
    {
        var levels = LevelCountCore;
        if (nodes.Length < Count || levelStarts.Length < levels + 1) throw new ArgumentException("Buffers too small.");
        if (_h is null) { if (levelStarts.Length > 0) levelStarts[0] = 0; return; }
        var acc = 0;
        for (var k = 0; k < levels; k++) { levelStarts[k] = acc; acc += _h.Widths[k]; }
        levelStarts[levels] = acc;
        var next = levels <= 256 ? stackalloc int[levels] : new int[levels];
        levelStarts[..levels].CopyTo(next);
        foreach (var n in this) nodes[next[_h.Rank.GetValueRefOrNullRef(n).Level]++] = n;
    }

    /// <summary>
    /// As <c>CopyByLevel(nodes, levelStarts)</c>, also filling <paramref name="predecessorCounts"/>[i] with the
    /// in-degree of <paramref name="nodes"/>[i] in <paramref name="graph"/> (O(1) each): the initial counters of a
    /// dependency-counting scheduler.
    /// </summary>
    internal void CopyByLevelCore<TGraph>(TGraph graph, Span<N> nodes, Span<int> levelStarts, Span<int> predecessorCounts)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        if (predecessorCounts.Length < Count) throw new ArgumentException("Buffer too small.", nameof(predecessorCounts));
        CopyByLevelCore(nodes, levelStarts);
        for (var i = 0; i < Count; i++) predecessorCounts[i] = graph.Predecessors(nodes[i]).Count;
    }

    /// <summary>The parallel evaluation groups (allocating convenience over <c>CopyByLevel(nodes, levelStarts)</c>).</summary>
    internal N[][] ToLevelsCore()
    {
        var nodes = new N[Count];
        var starts = new int[LevelCountCore + 1];
        CopyByLevelCore(nodes, starts);
        var r = new N[LevelCountCore][];
        for (var k = 0; k < r.Length; k++) r[k] = nodes[starts[k]..starts[k + 1]];
        return r;
    }

    /// <summary>
    /// Cost-weighted bottom levels for critical-path list scheduling: <c>bottom[n] = cost(n) + max bottom[successor]</c>,
    /// one reverse pass over the order, O(V + E). Run ready nodes in descending bottom level to shorten the makespan.
    /// Costs are supplied per call (static estimates or measured averages) rather than stored in the persistent order,
    /// since measured costs change every tick. Returns the critical path length; <paramref name="bottom"/> is cleared
    /// and refilled (reuse it across ticks).
    /// </summary>
    public double ComputeBottomLevels<TGraph>(TGraph graph, Func<N, double> cost, Dictionary<N, double> bottom)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        bottom.Clear();
        if (_h is null) return 0;
        double critical = 0;
        var slots = _h.Slots;
        for (var p = slots.Count - 1; p >= 0; p--)
        {
            var n = slots[p];
            if (TSentinel.IsNone(n)) continue;
            double tail = 0;
            foreach (var s in graph.Successors(n))
                if (bottom.TryGetValue(s, out var bs) && bs > tail) tail = bs;
            var b = cost(n) + tail;
            bottom[n] = b;
            if (b > critical) critical = b;
        }
        return critical;
    }

    /// <summary>True if <paramref name="a"/> comes before <paramref name="b"/> in the order.</summary>
    public bool Precedes(N a, N b)
    {
        if (!TryGetRank(a, out var ra)) Throw.KeyNotFound(a);
        if (!TryGetRank(b, out var rb)) Throw.KeyNotFound(b);
        return ra < rb;
    }

    public TopoCore<N, TSentinel, TEntry> Add(N node) => Mutate(node, static (s, n) => s.Add(n));
    public TopoCore<N, TSentinel, TEntry> Remove(N node) => Contains(node) ? Mutate(node, static (s, n) => s.Remove(n)) : this;

    /// <summary>Gives <paramref name="newNode"/> the position of <paramref name="oldNode"/> (rename).</summary>
    public TopoCore<N, TSentinel, TEntry> Replace(N oldNode, N newNode) => Mutate((oldNode, newNode), static (s, a) => s.Replace(a.oldNode, a.newNode));

    /// <summary>Restores the order after <paramref name="graph"/> gained from→to. Throws <see cref="DagCycleException{N}"/>.</summary>
    public TopoCore<N, TSentinel, TEntry> AddEdge<TGraph>(TGraph graph, N from, N to) where TGraph : IDagAdjacency<N>, allows ref struct
    {
        if (!TryAddEdge(graph, from, to, out var r, out var cycle)) Throw.Cycle(cycle);
        return r;
    }

    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out TopoCore<N, TSentinel, TEntry> result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var ok = AddEdgeCore(graph, from, to, wantCycle: true, out result, out cycle);
        return ok;
    }

    /// <summary>Probe: false on a cycle without building it (allocation-free failure path).</summary>
    public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, out TopoCore<N, TSentinel, TEntry> result)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => AddEdgeCore(graph, from, to, wantCycle: false, out result, out _);

    private bool AddEdgeCore<TGraph>(TGraph graph, N from, N to, bool wantCycle, out TopoCore<N, TSentinel, TEntry> result, out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        // fast path: two lookups (rank and level together), no transient, when the edge already agrees with both
        if (_h is null) Throw.KeyNotFound(from);
        ref readonly var eu = ref _h.Rank.GetValueRefOrNullRef(from);
        if (Unsafe.IsNullRef(in eu)) Throw.KeyNotFound(from);
        ref readonly var ev = ref _h.Rank.GetValueRefOrNullRef(to);
        if (Unsafe.IsNullRef(in ev)) Throw.KeyNotFound(to);
        if ((int)(eu.Rank - _h.Base) < (int)(ev.Rank - _h.Base) && (!TEntry.TracksLevels || eu.Level < ev.Level)) { result = this; cycle = null; return true; }

        var t = Transient.Rent(_h);
        try
        {
            if (!t.AddEdgeCore(graph, from, to, wantCycle, out cycle)) { result = this; return false; }
            result = t.ToPersistent();
            return true;
        }
        finally { Transient.Return(t); }
    }

    /// <summary>Restores the order after <paramref name="graph"/> gained all <paramref name="edges"/> (e.g. an
    /// <see cref="ImmDag{N, A, V}.Scope.AddedEdges"/> batch). On a cycle returns false, this unchanged, and one cycle.</summary>
    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out TopoCore<N, TSentinel, TEntry> result, [NotNullWhen(false)] out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var ok = AddEdgesCore(graph, edges, wantCycle: true, out result, out cycle);
        return ok;
    }

    /// <summary>Probe: false on a cycle without building it (allocation-free failure path).</summary>
    public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, out TopoCore<N, TSentinel, TEntry> result)
        where TGraph : IDagAdjacency<N>, allows ref struct
        => AddEdgesCore(graph, edges, wantCycle: false, out result, out _);

    private bool AddEdgesCore<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, bool wantCycle, out TopoCore<N, TSentinel, TEntry> result, out N[]? cycle)
        where TGraph : IDagAdjacency<N>, allows ref struct
    {
        var allInOrder = true; // fast path: nothing to do unless some edge disagrees with the order or the levels
        foreach (var (u, v) in edges)
        {
            if (EqualityComparer<N>.Default.Equals(u, v)) { allInOrder = false; break; }
            if (_h is null || !_h.Rank.TryGetValue(u, out var eu) || !_h.Rank.TryGetValue(v, out var ev)) continue;
            if ((int)(eu.Rank - _h.Base) >= (int)(ev.Rank - _h.Base) || (TEntry.TracksLevels && eu.Level >= ev.Level)) { allInOrder = false; break; }
        }
        if (allInOrder) { result = this; cycle = null; return true; }

        var t = Transient.Rent(_h);
        try
        {
            if (!t.AddEdgesCore(graph, edges, wantCycle, out cycle)) { result = this; return false; }
            result = t.ToPersistent();
            return true;
        }
        finally { Transient.Return(t); }
    }

    public TopoCore<N, TSentinel, TEntry> Mutate<TArg>(TArg arg, Action<Transient, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(_h);
        try { batch(t, arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public Transient ToTransient()
    {
        var t = new Transient();
        t.Reset(this);
        return t;
    }

    public TopoEnumerator<N, TSentinel> GetEnumerator() => new(_h?.Slots ?? default);
    public TopoCursor<N, TSentinel> GetCursor() => new(_h?.Slots ?? default);

    /// <summary>Nodes in topological order (span-chunked scan, holes skipped).</summary>

    /// <summary>
    /// Mutable view; all searches reuse scratch buffers held here (no per-call allocation once warm). Not thread-safe.
    /// After a failed <c>TryAddEdge(s)</c> the order (and levels) are unspecified: <see cref="Reset"/> to roll back the
    /// whole batch before using it again. Internally everything works in positions (rank - Base).
    /// </summary>
    public sealed class Transient
    {
        private const int CompactMin = 32;
        private const int PkBatchMax = 32; // out-of-order edges per batch handled edge-by-edge before switching to a window re-sort

        private readonly ImmMap<N, TEntry>.Transient _rank = new();
        private readonly ImmFlexVector<int>.Transient _widths = new();
        private readonly Dictionary<N, int> _required = [];
        private readonly PriorityQueue<N, int> _raiseQueue = new();
        private readonly ImmFlexVector<N>.Transient _slots = new();
        private uint _base;
        private int _dead;
        private TopoHead<N, TEntry>? _last;

        // scratch
        private readonly HashSet<N> _visited = [];
        private readonly Dictionary<N, N> _parent = [];
        private SparseUse _visitedUse, _parentUse;
        private readonly List<(N Node, int Pos)> _df = [], _db = [], _stack = [];
        private readonly List<int> _pool = [];
        private readonly HashSet<(N, N)> _pending = [];
        private readonly List<(N From, N To)> _late = [];
        private readonly Dictionary<N, int> _window = [];
        private readonly Dictionary<N, int> _indeg = [];
        private readonly PriorityQueue<N, int> _ready = new();
        private readonly List<N> _sorted = [];

        [ThreadStatic] private static Transient? t_cache;

        internal static Transient Rent(TopoHead<N, TEntry>? head)
        {
            var t = t_cache;
            if (t is null) t = new Transient();
            else t_cache = null;
            t.Reset(new(head));
            return t;
        }

        internal static void Return(Transient t)
        {
            t.Reset(default);
            t.TrimScratch();
            t_cache = t;
        }

        // Pooled: bound what a large batch leaves behind, so that small batches clear little (see Scratch).
        internal void TrimScratch()
        {
            Scratch.Trim(_visited);
            Scratch.Trim(_parent);
            Scratch.Trim(_df);
            Scratch.Trim(_db);
            Scratch.Trim(_stack);
            Scratch.Trim(_pool);
            Scratch.Trim(_pending);
            Scratch.Trim(_late);
            Scratch.Trim(_window);
            Scratch.Trim(_indeg);
            Scratch.Trim(_sorted);
        }

        public int Count => _rank.Count;
        public bool Contains(N node) => _rank.ContainsKey(node);

        public bool TryGetRank(N node, out long rank)
        {
            var found = TryPos(node, out var p);
            rank = p;
            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryPos(N node, out int pos)
        {
            var found = _rank.TryGetValue(node, out var e);
            pos = (int)(e.Rank - _base);
            return found;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint RankOf(int pos) => _base + (uint)pos;

        /// <summary>Appends a new node (last in the order); false if present. The sentinel value is rejected.</summary>
        public bool Add(N node)
        {
            if (TSentinel.IsNone(node)) Throw.Sentinel(node, nameof(node));
            ref var e = ref _rank.GetValueRefOrAddDefault(node, out var exists);
            if (exists) return false;
            e.Rank = RankOf(_slots.Count);
            e.Level = 0;
            _slots.Add(node);
            if (TEntry.TracksLevels)
            {
                if (_widths.Count == 0) _widths.Add(0);
                _widths.ItemRef(0)++;
            }
            _last = null;
            return true;
        }

        public bool Remove(N node)
        {
            if (!_rank.TryGetValue(node, out var entry)) return false;
            var pos = (int)(entry.Rank - _base);
            _rank.Remove(node);
            _last = null;
            if (TEntry.TracksLevels)
            {
                _widths.ItemRef(entry.Level)--;
                while (_widths.Count > 0 && _widths[_widths.Count - 1] == 0) _widths.RemoveLast();
            }
            if (_rank.Count == 0) { _slots.Clear(); _dead = 0; return true; }
            if (pos == 0)
            {
                var j = 1;
                while (TSentinel.IsNone(_slots[j])) j++;
                _dead -= j - 1;
                _slots.Skip(j);
                _base += (uint)j; // may wrap: positions are computed modulo 2^32
            }
            else if (pos == _slots.Count - 1)
            {
                var j = pos - 1;
                while (TSentinel.IsNone(_slots[j])) j--;
                _dead -= pos - 1 - j;
                _slots.Take(j + 1);
            }
            else
            {
                _slots.ItemRef(pos) = TSentinel.None;
                _dead++;
            }
            if (_dead > CompactMin && _dead > _rank.Count) Compact();
            return true;
        }

        public bool Replace(N oldNode, N newNode)
        {
            if (TSentinel.IsNone(newNode)) Throw.Sentinel(newNode, nameof(newNode));
            if (!_rank.TryGetValue(oldNode, out var e) || _rank.ContainsKey(newNode)) return false;
            _rank.Remove(oldNode);
            _rank[newNode] = e; // keeps position and level
            _slots.ItemRef((int)(e.Rank - _base)) = newNode;
            _last = null;
            return true;
        }

        private void Compact() => Rebase(0);

        /// <summary>Renumbers live nodes densely from <paramref name="newBase"/> (compaction; tests use it to start
        /// near uint.MaxValue and exercise wrap-around).</summary>
        internal void Rebase(uint newBase)
        {
            var old = _slots.ToPersistent();
            _slots.Clear();
            _base = newBase;
            foreach (ref readonly var n in old)
            {
                if (TSentinel.IsNone(n)) continue;
                _rank.GetValueRefOrAddDefault(n, out _).Rank = RankOf(_slots.Count);
                _slots.Add(n);
            }
            _dead = 0;
        }

        // ------------------------------------------------------------------------------------- single edge (PK)

        public bool TryAddEdge<TGraph>(TGraph graph, N from, N to, [NotNullWhen(false)] out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            var ok = AddEdgeCore(graph, from, to, wantCycle: true, out cycle);
            return ok;
        }

        /// <summary>Probe: false on a cycle without building it (allocation-free failure path).</summary>
        public bool TryAddEdge<TGraph>(TGraph graph, N from, N to)
            where TGraph : IDagAdjacency<N>, allows ref struct
            => AddEdgeCore(graph, from, to, wantCycle: false, out _);

        /// <summary>Order (PK), then levels (only once the edge is known not to close a cycle).</summary>
        internal bool AddEdgeCore<TGraph>(TGraph graph, N from, N to, bool wantCycle, out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            if (!Pk(graph, from, to, wantCycle, out cycle)) return false;
            if (TEntry.TracksLevels)
            {
                RequireEdge(from, to);
                PropagateLevels(graph);
            }
            return true;
        }

        /// <summary>Pearce–Kelly for from→to. Edges in <see cref="_pending"/> are treated as not yet in the graph.
        /// With <paramref name="wantCycle"/> false a cycle is only detected, never materialised.</summary>
        internal bool Pk<TGraph>(TGraph graph, N from, N to, bool wantCycle, out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            cycle = null;
            if (!TryPos(from, out var ub)) Throw.KeyNotFound(from);
            if (!TryPos(to, out var lb)) Throw.KeyNotFound(to);
            if (lb > ub) return true; // already consistent: O(1)
            if (EqualityComparer<N>.Default.Equals(from, to)) { if (wantCycle) cycle = [from, to]; return false; }

            _visitedUse.Clear(_visited);
            _parentUse.Clear(_parent);
            _df.Clear();
            _db.Clear();
            var checkPending = _pending.Count != 0;

            // forward from `to`, within positions (lb, ub): reaching `from` closes a cycle
            _stack.Clear();
            _stack.Add((to, lb));
            _visited.Add(to);
            while (_stack.Count > 0)
            {
                var (x, px) = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                _df.Add((x, px));
                foreach (var w in graph.Successors(x))
                {
                    if (checkPending && _pending.Contains((x, w))) continue;
                    if (!TryPos(w, out var pw)) continue;
                    if (pw < ub)
                    {
                        if (_visited.Add(w))
                        {
                            _parent[w] = x;
                            _stack.Add((w, pw));
                        }
                    }
                    else if (pw == ub)
                    {
                        if (wantCycle) cycle = CyclePath(to, x, w);
                        return false;
                    }
                }
            }

            // backward from `from`, within positions (lb, ub)
            _stack.Add((from, ub));
            _visited.Add(from);
            while (_stack.Count > 0)
            {
                var (x, px) = _stack[^1];
                _stack.RemoveAt(_stack.Count - 1);
                _db.Add((x, px));
                foreach (var w in graph.Predecessors(x))
                {
                    if (checkPending && _pending.Contains((w, x))) continue;
                    if (!TryPos(w, out var pw)) continue;
                    if (pw > lb && _visited.Add(w)) _stack.Add((w, pw));
                }
            }

            // reorder: all of δB (by position) then all of δF (by position) take the merged pool of their positions
            var db = CollectionsMarshal.AsSpan(_db);
            var df = CollectionsMarshal.AsSpan(_df);
            db.Sort(static (a, b) => a.Pos.CompareTo(b.Pos));
            df.Sort(static (a, b) => a.Pos.CompareTo(b.Pos));
            _pool.Clear();
            int i = 0, k = 0;
            while (i < db.Length || k < df.Length)
                _pool.Add(k == df.Length || (i < db.Length && db[i].Pos < df[k].Pos) ? db[i++].Pos : df[k++].Pos);
            var p = 0;
            foreach (var (n, op) in db) Assign(n, op, _pool[p++]);
            foreach (var (n, op) in df) Assign(n, op, _pool[p++]);
            _last = null;
            return true;
        }

        private void Assign(N node, int oldPos, int newPos)
        {
            if (oldPos == newPos) return;
            _rank.GetValueRefOrAddDefault(node, out _).Rank = RankOf(newPos); // keeps the level
            _slots.ItemRef(newPos) = node;
        }
        
        
        /// <summary>The cycle to ... x, from, to, read back through <see cref="_parent"/>. Cold: only a refused edge
        /// whose cycle the caller wants.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private N[] CyclePath(N to, N x, N w)
        {
            var len = 3;                                // count the chain, then fill it
            for (var c = x; !EqualityComparer<N>.Default.Equals(c, to); c = _parent[c]) len++;
            var path = new N[len];
            path[^1] = to;
            path[^2] = w;
            var at = len - 3;
            for (var c = x; ; c = _parent[c]) { path[at--] = c; if (EqualityComparer<N>.Default.Equals(c, to)) break; }
            return path;
        }

        // ------------------------------------------------------------------------------------- levels

        private void SetLevel(N node, int oldLevel, int newLevel)
        {
            _rank.GetValueRefOrAddDefault(node, out _).Level = newLevel;
            _widths.ItemRef(oldLevel)--;
            while (_widths.Count <= newLevel) _widths.Add(0);
            _widths.ItemRef(newLevel)++;
            while (_widths[_widths.Count - 1] == 0) _widths.RemoveLast();
        }

        /// <summary>Requires level(to) &gt; level(from); call <see cref="PropagateLevels"/> afterwards.</summary>
        private void RequireEdge(N from, N to)
        {
            var need = _rank.GetValueRefOrNullRef(from).Level + 1;
            if (_rank.GetValueRefOrNullRef(to).Level < need) Require(to, need);
        }

        private void Require(N node, int level)
        {
            if (_required.TryGetValue(node, out var r))
            {
                if (r < level) _required[node] = level;
                return;
            }
            _required[node] = level;
            TryPos(node, out var pos);
            _raiseQueue.Enqueue(node, pos);
        }

        /// <summary>
        /// Applies the required increases in topological (position) order: every predecessor of a node precedes it,
        /// so a node's requirement is final when it is dequeued and each node is raised at most once per call —
        /// O((affected nodes + their edges) · log n), never once per path.
        /// </summary>
        private void PropagateLevels<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
        {
            while (_raiseQueue.TryDequeue(out var x, out _))
            {
                _required.Remove(x, out var level);
                var current = _rank.GetValueRefOrNullRef(x).Level;
                if (level <= current) continue;
                SetLevel(x, current, level);
                _last = null;
                foreach (var s in graph.Successors(x))
                    if (_rank.TryGetValue(s, out var es) && es.Level <= level) Require(s, level + 1);
            }
        }

        internal bool TryGetLevelCore(N node, out int level)
        {
            var found = _rank.TryGetValue(node, out var e);
            level = e.Level;
            return found;
        }

        internal int LevelCountCore => _widths.Count;
        internal int LevelWidthCore(int level) => (uint)level < (uint)_widths.Count ? _widths[level] : 0;

        /// <summary>Exact (longest-path) levels: one pass in topological order, so predecessors are final first.</summary>
        internal void TightenCore<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
        {
            _widths.Clear();
            var slots = _slots.Peek();                      // leaf by leaf, no freeze: only _rank and _widths change below
            while (slots.MoveNext())
            {
                var n = slots.Current;
                if (TSentinel.IsNone(n)) continue;
                var level = 0;
                foreach (var q in graph.Predecessors(n))
                    if (_rank.TryGetValue(q, out var eq) && eq.Level + 1 > level) level = eq.Level + 1;
                if (_rank.GetValueRefOrNullRef(n).Level != level) _rank.GetValueRefOrAddDefault(n, out _).Level = level;
                while (_widths.Count <= level) _widths.Add(0);
                _widths.ItemRef(level)++;
            }
            _last = null;
        }

        // ------------------------------------------------------------------------------------- batch

        /// <summary>Restores the order after <paramref name="graph"/> gained <paramref name="edges"/>. Edges with an
        /// endpoint missing from the order (e.g. removed later in the same batch) are ignored.</summary>
        public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, [NotNullWhen(false)] out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            var ok = AddEdgesCore(graph, edges, wantCycle: true, out cycle);
            return ok;
        }

        /// <summary>Probe: false on a cycle without building it (allocation-free failure path).</summary>
        public bool TryAddEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges)
            where TGraph : IDagAdjacency<N>, allows ref struct
            => AddEdgesCore(graph, edges, wantCycle: false, out _);

        internal bool AddEdgesCore<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, bool wantCycle, out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            if (!OrderEdges(graph, edges, wantCycle, out cycle)) return false;
            if (!TEntry.TracksLevels) return true;
            foreach (var (u, v) in edges)
                if (_rank.ContainsKey(u) && _rank.ContainsKey(v)) RequireEdge(u, v);
            PropagateLevels(graph); // one topologically ordered pass for the whole batch
            return true;
        }

        private bool OrderEdges<TGraph>(TGraph graph, ReadOnlySpan<(N From, N To)> edges, bool wantCycle, out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            cycle = null;
            _pending.Clear();
            _late.Clear();
            foreach (var (u, v) in edges)
            {
                if (EqualityComparer<N>.Default.Equals(u, v)) { if (wantCycle) cycle = [u, v]; return false; }
                if (!TryPos(u, out var pu) || !TryPos(v, out var pv) || pu < pv) continue;
                if (_pending.Add((u, v))) _late.Add((u, v));
            }
            if (_late.Count == 0) return true;
            try
            {
                if (_late.Count > PkBatchMax) return WindowResort(graph, wantCycle, out cycle);
                foreach (var (u, v) in _late)
                {
                    _pending.Remove((u, v)); // from now on this edge is part of the graph PK sees
                    if (!Pk(graph, u, v, wantCycle, out cycle)) return false;
                }
                return true;
            }
            finally { _pending.Clear(); }
        }

        /// <summary>Kahn re-sort of the position window spanned by the out-of-order edges, ties broken by old position
        /// (so nodes move only when they must). Old edges respect the old order, hence every cycle lies in the window.</summary>
        private bool WindowResort<TGraph>(TGraph graph, bool wantCycle, out N[]? cycle)
            where TGraph : IDagAdjacency<N>, allows ref struct
        {
            cycle = null;
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var (u, v) in _late)
            {
                TryPos(v, out var pv);
                TryPos(u, out var pu);
                lo = Math.Min(lo, pv);
                hi = Math.Max(hi, pu);
            }

            _window.Clear();
            _pool.Clear();
            for (var p = lo; p <= hi; p++)
            {
                var n = _slots[p];
                if (TSentinel.IsNone(n)) continue;
                _window[n] = p;
                _pool.Add(p);
            }
            _indeg.Clear();
            foreach (var (n, _) in _window)
            {
                var d = 0;
                foreach (var w in graph.Predecessors(n)) if (_window.ContainsKey(w)) d++;
                _indeg[n] = d;
            }
            _ready.Clear();
            foreach (var (n, d) in _indeg) if (d == 0) _ready.Enqueue(n, _window[n]);
            _sorted.Clear();
            while (_ready.TryDequeue(out var n, out _))
            {
                _sorted.Add(n);
                _indeg[n] = -1;
                foreach (var s in graph.Successors(n))
                    if (_window.TryGetValue(s, out var ps) && --CollectionsMarshal.GetValueRefOrNullRef(_indeg, s) == 0)
                        _ready.Enqueue(s, ps);
            }
            if (_sorted.Count < _window.Count)
            {
                if (wantCycle) cycle = FindCycle(graph);
                return false;
            }
            for (var i = 0; i < _sorted.Count; i++) Assign(_sorted[i], _window[_sorted[i]], _pool[i]);
            _last = null;
            return true;
        }

        /// <summary>Every node left with indegree &gt; 0 has a remaining predecessor: walk predecessors until a repeat.</summary>
        private N[] FindCycle<TGraph>(TGraph graph) where TGraph : IDagAdjacency<N>, allows ref struct
        {
            N cur = default!;
            foreach (var (n, d) in _indeg) if (d > 0) { cur = n; break; }
            var seen = new Dictionary<N, int>();
            var path = new List<N>();
            while (!seen.ContainsKey(cur))
            {
                seen[cur] = path.Count;
                path.Add(cur);
                foreach (var w in graph.Predecessors(cur))
                    if (_indeg.TryGetValue(w, out var d) && d > 0) { cur = w; break; }
            }
            var c = path.GetRange(seen[cur], path.Count - seen[cur]); // walked backwards: reverse into edge direction
            c.Reverse();
            c.Add(c[0]);
            return [.. c];
        }

        // ------------------------------------------------------------------------------------- lifecycle

        public void Reset(TopoCore<N, TSentinel, TEntry> source)
        {
            var h = source._h;
            _rank.Reset(h?.Rank ?? default);
            _widths.Reset(h?.Widths ?? default);
            _slots.Reset(h?.Slots ?? default);
            _base = h?.Base ?? 0;
            _dead = h?.Dead ?? 0;
            _last = h;
        }

        public TopoCore<N, TSentinel, TEntry> ToPersistent()
        {
            var rank = _rank.ToPersistent();
            var slots = _slots.ToPersistent();
            var widths = TEntry.TracksLevels ? _widths.ToPersistent() : default;
            if (rank.IsEmpty) return default;
            var l = _last;
            if (l is not null && l.Rank.IsSameAs(rank) && l.Slots.IsSameAs(slots) && l.Widths.IsSameAs(widths)) return new(l);
            return new(_last = new TopoHead<N, TEntry> { Rank = rank, Slots = slots, Widths = widths, Base = _base, Dead = _dead });
        }

        public TopoEnumerator<N, TSentinel> GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

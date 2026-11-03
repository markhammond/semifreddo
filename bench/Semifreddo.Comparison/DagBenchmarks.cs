using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Semifreddo;
using Bcl = Semifreddo.Comparison.Dags.ImmutableDictionaryDag<long, long, long>;
using Lex = Semifreddo.Comparison.Dags.HashMapDag<long, long, long>;
using Order = Semifreddo.ImmPkTopoOrder<long, Semifreddo.MinValueSentinel<long>>;
using Sf = Semifreddo.ImmDag<long, long, long>;

namespace Semifreddo.Comparison;

/// <summary>
/// Persistent DAGs of N nodes (<c>long</c> ids, alternate keys and payloads) and about 2N edges, every intermediate
/// version a value of its own. No other library has one, so the baseline in each category is the DAG one would assemble
/// from System.Collections.Immutable (<see cref="Dags.ImmutableDictionaryDag{N, A, V}"/>), beside the same code over
/// LanguageExt's collections (<see cref="Dags.HashMapDag{N, A, V}"/>). Both check a new edge by a search from its
/// target, as <see cref="ImmDag{N, A, V}"/> does without an order; <c>_WithOrder</c> keeps an
/// <see cref="ImmPkTopoOrder{N, TSentinel}"/> in step instead, which proves each edge without searching the graph.
/// <para>The graph is made of groups of 32 nodes, each group a random DAG (edges in from up to two nodes earlier in a
/// random order of the group), so a search reaches at most a group and the costs are the collections'. The edges come
/// in shuffled, so about half disagree with the order the nodes were added in. Build adds the nodes and then the edges
/// one by one; Batch build adds them as one batch (Semifreddo's <c>Mutate</c>, the System.Collections.Immutable DAG's
/// builders; LanguageExt has none, so it makes the edits one by one: <c>_PerEdit</c>). On the built graph: Add edge adds
/// about N more edges, each to the built version; Refuse cycle tries each edge reversed, which closes a cycle (it counts
/// the refusals: each finds a cycle, not necessarily the same one); Remove node removes every node (and its edges) one
/// by one; Walk edges reads every successor set; Topological order lists the nodes in one (Kahn's algorithm, computed on
/// demand, except <c>_WithOrder</c>, which has one).</para>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DagBenchmarks
{
    private const int Group = 32;

    [ParamsSource(typeof(Sizes), nameof(Sizes.N))]
    public int N;

    private long[] _ids = [], _alts = [], _removals = [];
    private (long From, long To)[] _edges = [], _extra = [], _reversed = [];
    private Sf _sf;
    private Sf _sfOrdered;
    private Order _order;
    private Bcl _bcl;
    private Lex _lex = Lex.Empty;

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(42);
        var seen = new HashSet<long>();
        while (seen.Count < 2 * N) seen.Add(rnd.NextInt64());
        var all = seen.ToArray();
        _ids = all[..N];
        _alts = all[N..];
        var edges = new List<(long, long)>();
        var extra = new List<(long, long)>();
        for (var g = 0; g < N; g += Group)
        {
            var byRank = Enumerable.Range(g, Math.Min(Group, N - g)).OrderBy(_ => rnd.Next()).ToArray();   // the group's order
            var present = new HashSet<(int, int)>();
            for (var r = 1; r < byRank.Length; r++)
            {
                var to = byRank[r];
                for (var k = 0; k < Math.Min(r, 2); k++)
                {
                    var from = byRank[rnd.Next(r)];
                    if (present.Add((from, to))) edges.Add((_ids[from], _ids[to]));
                }
                for (var tries = 0; tries < 4; tries++)          // one more, held back for Add edge
                {
                    var from = byRank[rnd.Next(r)];
                    if (present.Add((from, to))) { extra.Add((_ids[from], _ids[to])); break; }
                }
            }
        }
        _edges = [.. edges.OrderBy(_ => rnd.Next())];
        _extra = [.. extra.OrderBy(_ => rnd.Next())];
        _reversed = [.. _edges.Select(e => (e.To, e.From))];
        _removals = [.. _ids.OrderBy(_ => rnd.Next())];

        _sf = BatchBuild(Sf.Empty);
        _order = ImmPkTopoOrder.CreateWithMinValueSentinel<long>();
        _sfOrdered = BatchBuild(ref _order);
        _bcl = BatchBuild(Bcl.Empty);
        _lex = Build(Lex.Empty);
        if (_sf.EdgeCount != _edges.Length || _sfOrdered.EdgeCount != _edges.Length || _bcl.EdgeCount != _edges.Length || _lex.EdgeCount != _edges.Length)
            throw new InvalidOperationException("The graphs differ.");
    }

    private Sf BatchBuild(Sf g) => g.Mutate(this, static (s, b) => b.AddAll(s));
    private Sf BatchBuild(ref Order o) => Sf.Empty.Mutate(ref o, this, static (s, b) => b.AddAll(s));

    private void AddAll(Sf.Scope s)
    {
        for (var i = 0; i < _ids.Length; i++) s.AddNode(_ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) s.AddEdge(f, t);
    }

    private Bcl BatchBuild(Bcl g) => g.Mutate(b =>
    {
        for (var i = 0; i < _ids.Length; i++) b.AddNode(_ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) b.AddEdge(f, t);
    });

    private Lex Build(Lex g)
    {
        for (var i = 0; i < _ids.Length; i++) g = g.AddNode(_ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) g = g.AddEdge(f, t);
        return g;
    }

    // ---------------------------------------------------------------------------------------------------- Build

    [Benchmark(Baseline = true), BenchmarkCategory("Build")]
    public int Build_SystemImmutable_ImmutableDictionaryDag()
    {
        var g = Bcl.Empty;
        for (var i = 0; i < _ids.Length; i++) g = g.AddNode(_ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) g = g.AddEdge(f, t);
        return g.EdgeCount;
    }

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Semifreddo_ImmDag()
    {
        var g = Sf.Empty;
        for (var i = 0; i < _ids.Length; i++) g = g.AddNode(_ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) g = g.AddEdge(f, t);
        return g.EdgeCount;
    }

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Semifreddo_ImmDag_WithOrder()
    {
        var g = Sf.Empty;
        var o = ImmPkTopoOrder.CreateWithMinValueSentinel<long>();
        for (var i = 0; i < _ids.Length; i++) g = g.AddNode(ref o, _ids[i], _alts[i], i);
        foreach (var (f, t) in _edges) g = g.AddEdge(ref o, f, t);
        return g.EdgeCount;
    }

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_LanguageExt_HashMapDag() => Build(Lex.Empty).EdgeCount;

    // ---------------------------------------------------------------------------------------------------- Batch build

    [Benchmark(Baseline = true), BenchmarkCategory("Batch build")]
    public int BatchBuild_SystemImmutable_ImmutableDictionaryDag_Builder() => BatchBuild(Bcl.Empty).EdgeCount;

    [Benchmark, BenchmarkCategory("Batch build")]
    public int BatchBuild_Semifreddo_ImmDag_Mutate() => BatchBuild(Sf.Empty).EdgeCount;

    [Benchmark, BenchmarkCategory("Batch build")]
    public int BatchBuild_Semifreddo_ImmDag_WithOrder_Mutate()
    {
        var o = ImmPkTopoOrder.CreateWithMinValueSentinel<long>();
        return BatchBuild(ref o).EdgeCount;
    }

    [Benchmark, BenchmarkCategory("Batch build")]
    public int BatchBuild_LanguageExt_HashMapDag_PerEdit() => Build(Lex.Empty).EdgeCount;

    // ---------------------------------------------------------------------------------------------------- Add edge

    [Benchmark(Baseline = true), BenchmarkCategory("Add edge")]
    public int AddEdge_SystemImmutable_ImmutableDictionaryDag()
    {
        var n = 0;
        foreach (var (f, t) in _extra) n += _bcl.AddEdge(f, t).EdgeCount;
        return n;
    }

    [Benchmark, BenchmarkCategory("Add edge")]
    public int AddEdge_Semifreddo_ImmDag()
    {
        var n = 0;
        foreach (var (f, t) in _extra) n += _sf.AddEdge(f, t).EdgeCount;
        return n;
    }

    [Benchmark, BenchmarkCategory("Add edge")]
    public int AddEdge_Semifreddo_ImmDag_WithOrder()
    {
        var n = 0;
        foreach (var (f, t) in _extra)
        {
            var o = _order;
            n += _sfOrdered.AddEdge(ref o, f, t).EdgeCount;
        }
        return n;
    }

    [Benchmark, BenchmarkCategory("Add edge")]
    public int AddEdge_LanguageExt_HashMapDag()
    {
        var n = 0;
        foreach (var (f, t) in _extra) n += _lex.AddEdge(f, t).EdgeCount;
        return n;
    }

    // ---------------------------------------------------------------------------------------------------- Refuse cycle

    [Benchmark(Baseline = true), BenchmarkCategory("Refuse cycle")]
    public int RefuseCycle_SystemImmutable_ImmutableDictionaryDag()
    {
        var n = 0;
        foreach (var (f, t) in _reversed)
            if (!_bcl.TryAddEdge(f, t, out _, out _)) n++;
        return n;
    }

    [Benchmark, BenchmarkCategory("Refuse cycle")]
    public int RefuseCycle_Semifreddo_ImmDag()
    {
        var n = 0;
        foreach (var (f, t) in _reversed)
            if (!_sf.TryAddEdge(f, t, out _, out _)) n++;
        return n;
    }

    [Benchmark, BenchmarkCategory("Refuse cycle")]
    public int RefuseCycle_Semifreddo_ImmDag_WithOrder()
    {
        var n = 0;
        foreach (var (f, t) in _reversed)
        {
            var o = _order;
            if (!_sfOrdered.TryAddEdge(ref o, f, t, out _, out _)) n++;
        }
        return n;
    }

    [Benchmark, BenchmarkCategory("Refuse cycle")]
    public int RefuseCycle_LanguageExt_HashMapDag()
    {
        var n = 0;
        foreach (var (f, t) in _reversed)
            if (!_lex.TryAddEdge(f, t, out _, out _)) n++;
        return n;
    }

    // ---------------------------------------------------------------------------------------------------- Remove node

    [Benchmark(Baseline = true), BenchmarkCategory("Remove node")]
    public int RemoveNode_SystemImmutable_ImmutableDictionaryDag()
    {
        var g = _bcl;
        foreach (var id in _removals) g = g.RemoveNode(id);
        return g.NodeCount;
    }

    [Benchmark, BenchmarkCategory("Remove node")]
    public int RemoveNode_Semifreddo_ImmDag()
    {
        var g = _sf;
        foreach (var id in _removals) g = g.RemoveNode(id);
        return g.NodeCount;
    }

    [Benchmark, BenchmarkCategory("Remove node")]
    public int RemoveNode_Semifreddo_ImmDag_WithOrder()
    {
        var g = _sfOrdered;
        var o = _order;
        foreach (var id in _removals) g = g.RemoveNode(ref o, id);
        return g.NodeCount;
    }

    [Benchmark, BenchmarkCategory("Remove node")]
    public int RemoveNode_LanguageExt_HashMapDag()
    {
        var g = _lex;
        foreach (var id in _removals) g = g.RemoveNode(id);
        return g.NodeCount;
    }

    // ---------------------------------------------------------------------------------------------------- Walk edges

    [Benchmark(Baseline = true), BenchmarkCategory("Walk edges")]
    public long Walk_SystemImmutable_ImmutableDictionaryDag()
    {
        long s = 0;
        foreach (var id in _ids)
            foreach (var x in _bcl.Successors(id)) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Walk edges")]
    public long Walk_Semifreddo_ImmDag()
    {
        long s = 0;
        foreach (var id in _ids)
            foreach (var x in _sf.Successors(id)) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Walk edges")]
    public long Walk_LanguageExt_HashMapDag()
    {
        long s = 0;
        foreach (var id in _ids)
            foreach (var x in _lex.Successors(id)) s += x;
        return s;
    }

    // ---------------------------------------------------------------------------------------------------- Topological order

    [Benchmark(Baseline = true), BenchmarkCategory("Topological order")]
    public int Topo_SystemImmutable_ImmutableDictionaryDag() => _bcl.TopologicalOrder().Length;

    [Benchmark, BenchmarkCategory("Topological order")]
    public int Topo_Semifreddo_ImmDag() => Kahn(_sf).Length;

    [Benchmark, BenchmarkCategory("Topological order")]
    public int Topo_Semifreddo_ImmDag_WithOrder()
    {
        var a = new long[_order.Count];
        var i = 0;
        foreach (var x in _order) a[i++] = x;
        return a.Length;
    }

    [Benchmark, BenchmarkCategory("Topological order")]
    public int Topo_LanguageExt_HashMapDag() => _lex.TopologicalOrder().Length;

    // Kahn's algorithm, as the other DAGs compute it.
    private static long[] Kahn(Sf g)
    {
        var order = new long[g.NodeCount];
        var pending = new Dictionary<long, int>(g.NodeCount);
        int head = 0, tail = 0;
        foreach (var n in g)
        {
            if (n.Predecessors.Count == 0) order[tail++] = n.Id;
            else pending[n.Id] = n.Predecessors.Count;
        }
        while (head < tail)
            foreach (var s in g.Successors(order[head++]))
                if (--CollectionsMarshal.GetValueRefOrNullRef(pending, s) == 0) order[tail++] = s;
        return order;
    }
}

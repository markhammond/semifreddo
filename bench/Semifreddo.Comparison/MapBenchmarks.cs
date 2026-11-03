using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using LanguageExt;
using Semifreddo;
using FxMap = FSharpx.Collections.PersistentHashMap<long, long>;
#if DURABLE7
using D7Map = Durable7.Hamt.PersistentHashMap<long, long>;
#endif

namespace Semifreddo.Comparison;

/// <summary>
/// Persistent hash maps of <c>long</c> to <c>long</c>: each operation N times, every intermediate version a value of its
/// own. The baseline in each category is <see cref="ImmutableDictionary{TKey, TValue}"/> (System.Collections.Immutable,
/// an AVL tree of hash buckets); the rest are hash tries (CHAMP: Semifreddo, LanguageExt, Durable7; Clojure's HAMT:
/// FSharpx). A hit reads the value through each map's own single-lookup path (<c>TryGetValue</c>, or an indexer where
/// that is the only one); a miss asks <c>ContainsKey</c> everywhere.
/// <para>The Batch categories make N edits to the existing map as one batch, giving one new version, through each
/// library's batch facility: a transient (Semifreddo's <c>Mutate</c>, Durable7's <c>ToTransient</c>), a builder
/// (System.Collections.Immutable) or a range method (LanguageExt). FSharpx has none public, so it does the edits one by one
/// (<c>_PerEdit</c>). Batch update makes the Update edits; Batch insert adds N absent keys; Batch remove, the Remove
/// edits.</para>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class MapBenchmarks
{
    [ParamsSource(typeof(Sizes), nameof(Sizes.N))]
    public int N;

    private long[] _keys = [];
    private long[] _absent = [];      // keys in none of the maps
    private KeyValuePair<long, long>[] _pairs = [], _updates = [], _inserts = [];
    private ImmMap<long, long> _sf;
    private ImmutableDictionary<long, long> _bcl = ImmutableDictionary<long, long>.Empty;
    private HashMap<long, long> _lex;
    private FxMap _fx = FxMap.Empty();
#if DURABLE7
    private D7Map _d7 = D7Map.Empty;
#endif

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(42);
        var seen = new System.Collections.Generic.HashSet<long>();
        while (seen.Count < 2 * N) seen.Add(rnd.NextInt64());
        var all = seen.ToArray();
        _keys = all[..N];
        _absent = all[N..];
        _pairs = _keys.Select(k => KeyValuePair.Create(k, k)).ToArray();
        _updates = _keys.Select(k => KeyValuePair.Create(k, k + 1)).ToArray();
        _inserts = _absent.Select(k => KeyValuePair.Create(k, k)).ToArray();
        _sf = ImmMap.Create<long, long>(_pairs);
        _bcl = ImmutableDictionary.CreateRange(_pairs);
        _lex = HashMap<long, long>.Empty.AddRange(_pairs);
        _fx = FSharpx.Collections.PersistentHashMap.ofSeq(_keys.Select(k => Tuple.Create(k, k)));
#if DURABLE7
        _d7 = D7Map.CreateRange(_pairs);
#endif
    }

    // ---------------------------------------------------------------- Add: N inserts into an empty map

    [Benchmark(Baseline = true), BenchmarkCategory("Add")]
    public int Add_SystemImmutable_ImmutableDictionary()
    {
        var m = ImmutableDictionary<long, long>.Empty;
        foreach (var k in _keys) m = m.SetItem(k, k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Add")]
    public int Add_Semifreddo_ImmMap()
    {
        var m = ImmMap<long, long>.Empty;
        foreach (var k in _keys) m = m.SetItem(k, k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Add")]
    public int Add_LanguageExt_HashMap()
    {
        var m = HashMap<long, long>.Empty;
        foreach (var k in _keys) m = m.AddOrUpdate(k, k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Add")]
    public int Add_FSharpx_PersistentHashMap()
    {
        var m = FxMap.Empty();
        foreach (var k in _keys) m = m.Add(k, k);
        return m.Count;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Add")]
    public int Add_Durable7_PersistentHashMap()
    {
        var m = D7Map.Empty;
        foreach (var k in _keys) m = m.SetItem(k, k);
        return m.Count;
    }
#endif

    // ---------------------------------------------------------------- Build: from N pairs, each library's bulk path

    [Benchmark(Baseline = true), BenchmarkCategory("Build")]
    public int Build_SystemImmutable_ImmutableDictionary() => ImmutableDictionary.CreateRange(_pairs).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Semifreddo_ImmMap() => ImmMap.Create<long, long>(_pairs).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_LanguageExt_HashMap() => HashMap<long, long>.Empty.AddRange(_pairs).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_FSharpx_PersistentHashMap()
        => FSharpx.Collections.PersistentHashMap.ofSeq(_keys.Select(k => Tuple.Create(k, k))).Count;   // F# takes tuples

#if DURABLE7
    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Durable7_PersistentHashMap() => D7Map.CreateRange(_pairs).Count;
#endif

    // ---------------------------------------------------------------- Lookup (hit): N keys present

    [Benchmark(Baseline = true), BenchmarkCategory("Lookup (hit)")]
    public long Hit_SystemImmutable_ImmutableDictionary()
    {
        long s = 0;
        foreach (var k in _keys) if (_bcl.TryGetValue(k, out var v)) s += v;
        return s;
    }

    [Benchmark, BenchmarkCategory("Lookup (hit)")]
    public long Hit_Semifreddo_ImmMap()
    {
        long s = 0;
        foreach (var k in _keys) if (_sf.TryGetValue(k, out var v)) s += v;
        return s;
    }

    [Benchmark, BenchmarkCategory("Lookup (hit)")]
    public long Hit_LanguageExt_HashMap()
    {
        long s = 0;
        foreach (var k in _keys) s += _lex[k];
        return s;
    }

    [Benchmark, BenchmarkCategory("Lookup (hit)")]
    public long Hit_FSharpx_PersistentHashMap()
    {
        long s = 0;
        foreach (var k in _keys) s += _fx[k];
        return s;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Lookup (hit)")]
    public long Hit_Durable7_PersistentHashMap()
    {
        long s = 0;
        foreach (var k in _keys) if (_d7.TryGetValue(k, out var v)) s += v;
        return s;
    }
#endif

    // ---------------------------------------------------------------- Lookup (miss): N keys absent

    [Benchmark(Baseline = true), BenchmarkCategory("Lookup (miss)")]
    public int Miss_SystemImmutable_ImmutableDictionary()
    {
        var n = 0;
        foreach (var k in _absent) if (_bcl.ContainsKey(k)) n++;
        return n;
    }

    [Benchmark, BenchmarkCategory("Lookup (miss)")]
    public int Miss_Semifreddo_ImmMap()
    {
        var n = 0;
        foreach (var k in _absent) if (_sf.ContainsKey(k)) n++;
        return n;
    }

    [Benchmark, BenchmarkCategory("Lookup (miss)")]
    public int Miss_LanguageExt_HashMap()
    {
        var n = 0;
        foreach (var k in _absent) if (_lex.ContainsKey(k)) n++;
        return n;
    }

    [Benchmark, BenchmarkCategory("Lookup (miss)")]
    public int Miss_FSharpx_PersistentHashMap()
    {
        var n = 0;
        foreach (var k in _absent) if (_fx.ContainsKey(k)) n++;
        return n;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Lookup (miss)")]
    public int Miss_Durable7_PersistentHashMap()
    {
        var n = 0;
        foreach (var k in _absent) if (_d7.ContainsKey(k)) n++;
        return n;
    }
#endif

    // ---------------------------------------------------------------- Update: N replacements of present keys

    [Benchmark(Baseline = true), BenchmarkCategory("Update")]
    public int Update_SystemImmutable_ImmutableDictionary()
    {
        var m = _bcl;
        foreach (var k in _keys) m = m.SetItem(k, k + 1);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_Semifreddo_ImmMap()
    {
        var m = _sf;
        foreach (var k in _keys) m = m.SetItem(k, k + 1);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_LanguageExt_HashMap()
    {
        var m = _lex;
        foreach (var k in _keys) m = m.SetItem(k, k + 1);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_FSharpx_PersistentHashMap()
    {
        var m = _fx;
        foreach (var k in _keys) m = m.Add(k, k + 1);
        return m.Count;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Update")]
    public int Update_Durable7_PersistentHashMap()
    {
        var m = _d7;
        foreach (var k in _keys) m = m.SetItem(k, k + 1);
        return m.Count;
    }
#endif

    // ---------------------------------------------------------------- Remove: every key, one at a time

    [Benchmark(Baseline = true), BenchmarkCategory("Remove")]
    public int Remove_SystemImmutable_ImmutableDictionary()
    {
        var m = _bcl;
        foreach (var k in _keys) m = m.Remove(k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Remove")]
    public int Remove_Semifreddo_ImmMap()
    {
        var m = _sf;
        foreach (var k in _keys) m = m.Remove(k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Remove")]
    public int Remove_LanguageExt_HashMap()
    {
        var m = _lex;
        foreach (var k in _keys) m = m.Remove(k);
        return m.Count;
    }

    [Benchmark, BenchmarkCategory("Remove")]
    public int Remove_FSharpx_PersistentHashMap()
    {
        var m = _fx;
        foreach (var k in _keys) m = m.Remove(k);
        return m.Count;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Remove")]
    public int Remove_Durable7_PersistentHashMap()
    {
        var m = _d7;
        foreach (var k in _keys) m = m.Remove(k);
        return m.Count;
    }
#endif

    // ---------------------------------------------------------------- Iterate: foreach over all N entries

    [Benchmark(Baseline = true), BenchmarkCategory("Iterate")]
    public long Iterate_SystemImmutable_ImmutableDictionary()
    {
        long s = 0;
        foreach (var kv in _bcl) s += kv.Value;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_Semifreddo_ImmMap()
    {
        long s = 0;
        foreach (var kv in _sf) s += kv.Value;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_LanguageExt_HashMap()
    {
        long s = 0;
        foreach (var (_, v) in _lex) s += v;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_FSharpx_PersistentHashMap()
    {
        long s = 0;
        foreach (var kv in _fx) s += kv.Item2;
        return s;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_Durable7_PersistentHashMap()
    {
        long s = 0;
        foreach (var (_, v) in _d7) s += v;
        return s;
    }
#endif

    // ---------------------------------------------------------------- Batch update: the Update edits, as one batch

    [Benchmark(Baseline = true), BenchmarkCategory("Batch update")]
    public int BatchUpdate_SystemImmutable_ImmutableDictionary_Builder()
    {
        var b = _bcl.ToBuilder();
        foreach (var k in _keys) b[k] = k + 1;
        return b.ToImmutable().Count;
    }

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_Semifreddo_ImmMap_Mutate()
        => _sf.Mutate(_keys, static (s, ks) => { foreach (var k in ks) s.SetItem(k, k + 1); }).Count;

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_LanguageExt_HashMap_SetItems() => _lex.SetItems(_updates).Count;

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_FSharpx_PersistentHashMap_PerEdit() => Update_FSharpx_PersistentHashMap();

#if DURABLE7
    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_Durable7_PersistentHashMap_Transient()
    {
        var t = _d7.ToTransient();
        foreach (var k in _keys) t.SetItem(k, k + 1);
        return t.Persist().Count;
    }
#endif

    // ---------------------------------------------------------------- Batch insert: N absent keys, as one batch

    [Benchmark(Baseline = true), BenchmarkCategory("Batch insert")]
    public int BatchInsert_SystemImmutable_ImmutableDictionary_Builder()
    {
        var b = _bcl.ToBuilder();
        foreach (var k in _absent) b[k] = k;
        return b.ToImmutable().Count;
    }

    [Benchmark, BenchmarkCategory("Batch insert")]
    public int BatchInsert_Semifreddo_ImmMap_Mutate()
        => _sf.Mutate(_absent, static (s, ks) => { foreach (var k in ks) s.SetItem(k, k); }).Count;

    [Benchmark, BenchmarkCategory("Batch insert")]
    public int BatchInsert_LanguageExt_HashMap_AddRange() => _lex.AddRange(_inserts).Count;

    [Benchmark, BenchmarkCategory("Batch insert")]
    public int BatchInsert_FSharpx_PersistentHashMap_PerEdit()
    {
        var m = _fx;
        foreach (var k in _absent) m = m.Add(k, k);
        return m.Count;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Batch insert")]
    public int BatchInsert_Durable7_PersistentHashMap_Transient()
    {
        var t = _d7.ToTransient();
        foreach (var k in _absent) t.SetItem(k, k);
        return t.Persist().Count;
    }
#endif

    // ---------------------------------------------------------------- Batch remove: the Remove edits, as one batch

    [Benchmark(Baseline = true), BenchmarkCategory("Batch remove")]
    public int BatchRemove_SystemImmutable_ImmutableDictionary_Builder()
    {
        var b = _bcl.ToBuilder();
        foreach (var k in _keys) b.Remove(k);
        return b.ToImmutable().Count;
    }

    [Benchmark, BenchmarkCategory("Batch remove")]
    public int BatchRemove_Semifreddo_ImmMap_Mutate()
        => _sf.Mutate(_keys, static (s, ks) => { foreach (var k in ks) s.Remove(k); }).Count;

    [Benchmark, BenchmarkCategory("Batch remove")]
    public int BatchRemove_LanguageExt_HashMap_RemoveRange() => _lex.RemoveRange(_keys).Count;

    [Benchmark, BenchmarkCategory("Batch remove")]
    public int BatchRemove_FSharpx_PersistentHashMap_PerEdit() => Remove_FSharpx_PersistentHashMap();

#if DURABLE7
    [Benchmark, BenchmarkCategory("Batch remove")]
    public int BatchRemove_Durable7_PersistentHashMap_Transient()
    {
        var t = _d7.ToTransient();
        foreach (var k in _keys) t.Remove(k);
        return t.Persist().Count;
    }
#endif
}

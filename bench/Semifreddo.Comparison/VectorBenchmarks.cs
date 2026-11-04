using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using FSharpx.Collections;
using LanguageExt;
#if DURABLE7
using Durable7.FingerTree;
#endif

namespace Semifreddo.Comparison;

/// <summary>
/// Persistent vectors of <c>long</c>: each operation N times, every intermediate version a value of its own. The
/// baseline in each category is <see cref="ImmutableList{T}"/> (System.Collections.Immutable, an AVL tree).
/// LanguageExt's <c>Seq</c> has no update by index, so it sits out Update. Branching append appends twice to every
/// version: Append's best case for a structure that appends in place (LanguageExt's <c>Seq</c>) holds only for one.
/// <para>The Batch categories make the same edits to an existing vector as one batch, giving one new version, through
/// each library's batch facility: a transient (Semifreddo's <c>Mutate</c>), a builder (System.Collections.Immutable,
/// Durable7's appends) or a range method. A library without one does the edits one by one (<c>_PerEdit</c>), as in
/// the per-edit categories.</para>
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class VectorBenchmarks
{
    [ParamsSource(typeof(Sizes), nameof(Sizes.N))]
    public int N;

    private long[] _items = [];
    private int[] _at = [];          // random indexes, the same for every contender
    private ImmVector<long> _sf;
    private ImmFlexVector<long> _sfFlex;
    private ImmutableList<long> _bcl = ImmutableList<long>.Empty;
    private Seq<long> _lexSeq;
    private Lst<long> _lexLst;
    private PersistentVector<long> _fx = PersistentVector<long>.Empty();
#if DURABLE7
    private RrbVector<long> _d7 = RrbVector<long>.Empty;
#endif

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(42);
        _items = Enumerable.Range(0, N).Select(_ => rnd.NextInt64()).ToArray();
        _at = Enumerable.Range(0, N).Select(_ => rnd.Next(N)).ToArray();
        _sf = ImmVector.Create<long>(_items);
        _sfFlex = ImmFlexVector.Create<long>(_items);
        _bcl = ImmutableList.CreateRange(_items);
        _lexSeq = Prelude.toSeq(_items).Strict();
        _lexLst = Prelude.toList(_items);
        _fx = PersistentVector.ofSeq(_items);
#if DURABLE7
        _d7 = RrbVector<long>.CreateRange(_items);
#endif
    }

    // ---------------------------------------------------------------- Append: N appends to an empty vector

    [Benchmark(Baseline = true), BenchmarkCategory("Append")]
    public int Append_SystemImmutable_ImmutableList()
    {
        var v = ImmutableList<long>.Empty;
        foreach (var x in _items) v = v.Add(x);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Append")]
    public int Append_Semifreddo_ImmVector()
    {
        var v = ImmVector<long>.Empty;
        foreach (var x in _items) v = v.Add(x);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Append")]
    public int Append_Semifreddo_ImmFlexVector()
    {
        var v = ImmFlexVector<long>.Empty;
        foreach (var x in _items) v = v.Add(x);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Append")]
    public int Append_LanguageExt_Seq()
    {
        var v = Seq<long>.Empty;
        foreach (var x in _items) v = v.Add(x);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Append")]
    public int Append_LanguageExt_Lst()
    {
        var v = Lst<long>.Empty;
        foreach (var x in _items) v = v.Add(x);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Append")]
    public int Append_FSharpx_PersistentVector()
    {
        var v = PersistentVector<long>.Empty();
        foreach (var x in _items) v = v.Conj(x);
        return v.Length;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Append")]
    public int Append_Durable7_RrbVector()
    {
        var v = RrbVector<long>.Empty;
        foreach (var x in _items) v = v.AddLast(x);
        return v.Count;
    }
#endif

    // ---------------------------------------------------------------- Build: from an array, each library's bulk path

    [Benchmark(Baseline = true), BenchmarkCategory("Build")]
    public int Build_SystemImmutable_ImmutableList() => ImmutableList.CreateRange(_items).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Semifreddo_ImmVector() => ImmVector.Create<long>(_items).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Semifreddo_ImmFlexVector() => ImmFlexVector.Create<long>(_items).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_LanguageExt_Seq() => Prelude.toSeq(_items).Strict().Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_LanguageExt_Lst() => Prelude.toList(_items).Count;

    [Benchmark, BenchmarkCategory("Build")]
    public int Build_FSharpx_PersistentVector() => PersistentVector.ofSeq(_items).Length;

#if DURABLE7
    [Benchmark, BenchmarkCategory("Build")]
    public int Build_Durable7_RrbVector() => RrbVector<long>.CreateRange(_items).Count;
#endif

    // ---------------------------------------------------------------- Index: N random reads

    [Benchmark(Baseline = true), BenchmarkCategory("Index")]
    public long Index_SystemImmutable_ImmutableList()
    {
        long s = 0;
        foreach (var i in _at) s += _bcl[i];
        return s;
    }

    [Benchmark, BenchmarkCategory("Index")]
    public long Index_Semifreddo_ImmVector()
    {
        long s = 0;
        foreach (var i in _at) s += _sf[i];
        return s;
    }

    [Benchmark, BenchmarkCategory("Index")]
    public long Index_Semifreddo_ImmFlexVector()
    {
        long s = 0;
        foreach (var i in _at) s += _sfFlex[i];
        return s;
    }

    [Benchmark, BenchmarkCategory("Index")]
    public long Index_LanguageExt_Seq()
    {
        long s = 0;
        foreach (var i in _at) s += _lexSeq[i];
        return s;
    }

    [Benchmark, BenchmarkCategory("Index")]
    public long Index_LanguageExt_Lst()
    {
        long s = 0;
        foreach (var i in _at) s += _lexLst[i];
        return s;
    }

    [Benchmark, BenchmarkCategory("Index")]
    public long Index_FSharpx_PersistentVector()
    {
        long s = 0;
        foreach (var i in _at) s += _fx[i];
        return s;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Index")]
    public long Index_Durable7_RrbVector()
    {
        long s = 0;
        foreach (var i in _at) s += _d7[i];
        return s;
    }
#endif

    // ---------------------------------------------------------------- Update: N random replacements, each a new version

    [Benchmark(Baseline = true), BenchmarkCategory("Update")]
    public int Update_SystemImmutable_ImmutableList()
    {
        var v = _bcl;
        foreach (var i in _at) v = v.SetItem(i, i);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_Semifreddo_ImmVector()
    {
        var v = _sf;
        foreach (var i in _at) v = v.SetItem(i, i);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_Semifreddo_ImmFlexVector()
    {
        var v = _sfFlex;
        foreach (var i in _at) v = v.SetItem(i, i);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_LanguageExt_Lst()
    {
        var v = _lexLst;
        foreach (var i in _at) v = v.SetItem(i, i);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("Update")]
    public int Update_FSharpx_PersistentVector()
    {
        var v = _fx;
        foreach (var i in _at) v = v.Update(i, i);
        return v.Length;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Update")]
    public int Update_Durable7_RrbVector()
    {
        var v = _d7;
        foreach (var i in _at) v = v.SetItem(i, i);
        return v.Count;
    }
#endif

    // ---------------------------------------------------------------- Iterate: foreach over all N

    [Benchmark(Baseline = true), BenchmarkCategory("Iterate")]
    public long Iterate_SystemImmutable_ImmutableList()
    {
        long s = 0;
        foreach (var x in _bcl) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_Semifreddo_ImmVector()
    {
        long s = 0;
        foreach (var x in _sf) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_Semifreddo_ImmFlexVector()
    {
        long s = 0;
        foreach (var x in _sfFlex) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_LanguageExt_Seq()
    {
        long s = 0;
        foreach (var x in _lexSeq) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_LanguageExt_Lst()
    {
        long s = 0;
        foreach (var x in _lexLst) s += x;
        return s;
    }

    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_FSharpx_PersistentVector()
    {
        long s = 0;
        foreach (var x in _fx) s += x;
        return s;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Iterate")]
    public long Iterate_Durable7_RrbVector()
    {
        long s = 0;
        foreach (var x in _d7) s += x;
        return s;
    }
#endif

    // ---------------------------------------------------------------- RemoveLast: drain all N from the end

    [Benchmark(Baseline = true), BenchmarkCategory("RemoveLast")]
    public int RemoveLast_SystemImmutable_ImmutableList()
    {
        var v = _bcl;
        while (!v.IsEmpty) v = v.RemoveAt(v.Count - 1);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_Semifreddo_ImmVector()
    {
        var v = _sf;
        while (!v.IsEmpty) v = v.RemoveLast();
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_Semifreddo_ImmFlexVector()
    {
        var v = _sfFlex;
        while (!v.IsEmpty) v = v.RemoveLast();
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_LanguageExt_Seq()
    {
        var v = _lexSeq;
        while (!v.IsEmpty) v = v.Init;
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_LanguageExt_Lst()
    {
        var v = _lexLst;
        while (!v.IsEmpty) v = v.RemoveAt(v.Count - 1);
        return v.Count;
    }

    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_FSharpx_PersistentVector()
    {
        var v = _fx;
        while (!v.IsEmpty) v = v.Initial;
        return v.Length;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("RemoveLast")]
    public int RemoveLast_Durable7_RrbVector()
    {
        var v = _d7;
        while (v.TryRemoveLast(out var next, out _)) v = next;
        return v.Count;
    }
#endif

    // ---------------------------------------------------------------- Branching append: two appends to each version
    // N steps from an empty vector, each appending twice to the same version (two branches) and going on from the
    // second: a structure that appends in place into spare capacity, valid only for the first append, copies.

    [Benchmark(Baseline = true), BenchmarkCategory("Branching append")]
    public int BranchingAppend_SystemImmutable_ImmutableList()
    {
        var v = ImmutableList<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.Add(x).Count; v = v.Add(~x); }
        return n + v.Count;
    }

    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_Semifreddo_ImmVector()
    {
        var v = ImmVector<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.Add(x).Count; v = v.Add(~x); }
        return n + v.Count;
    }

    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_Semifreddo_ImmFlexVector()
    {
        var v = ImmFlexVector<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.Add(x).Count; v = v.Add(~x); }
        return n + v.Count;
    }

    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_LanguageExt_Seq()
    {
        var v = Seq<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.Add(x).Count; v = v.Add(~x); }
        return n + v.Count;
    }

    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_LanguageExt_Lst()
    {
        var v = Lst<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.Add(x).Count; v = v.Add(~x); }
        return n + v.Count;
    }

    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_FSharpx_PersistentVector()
    {
        var v = PersistentVector<long>.Empty();
        var n = 0;
        foreach (var x in _items) { n += v.Conj(x).Length; v = v.Conj(~x); }
        return n + v.Length;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Branching append")]
    public int BranchingAppend_Durable7_RrbVector()
    {
        var v = RrbVector<long>.Empty;
        var n = 0;
        foreach (var x in _items) { n += v.AddLast(x).Count; v = v.AddLast(~x); }
        return n + v.Count;
    }
#endif

    // ---------------------------------------------------------------- Batch update: the Update edits, as one batch

    [Benchmark(Baseline = true), BenchmarkCategory("Batch update")]
    public int BatchUpdate_SystemImmutable_ImmutableList_Builder()
    {
        var b = _bcl.ToBuilder();
        foreach (var i in _at) b[i] = i;
        return b.ToImmutable().Count;
    }

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_Semifreddo_ImmVector_Mutate()
        => _sf.Mutate(_at, static (s, at) => { foreach (var i in at) s.ItemRef(i) = i; }).Count;

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_Semifreddo_ImmFlexVector_Mutate()
        => _sfFlex.Mutate(_at, static (s, at) => { foreach (var i in at) s.ItemRef(i) = i; }).Count;

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_LanguageExt_Lst_PerEdit() => Update_LanguageExt_Lst();

    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_FSharpx_PersistentVector_PerEdit() => Update_FSharpx_PersistentVector();

#if DURABLE7
    [Benchmark, BenchmarkCategory("Batch update")]
    public int BatchUpdate_Durable7_RrbVector_PerEdit() => Update_Durable7_RrbVector();
#endif

    // ---------------------------------------------------------------- Batch append: N more items onto the existing N

    [Benchmark(Baseline = true), BenchmarkCategory("Batch append")]
    public int BatchAppend_SystemImmutable_ImmutableList_AddRange() => _bcl.AddRange(_items).Count;

    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_Semifreddo_ImmVector_AddRange() => _sf.AddRange(_items).Count;

    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_Semifreddo_ImmFlexVector_AddRange() => _sfFlex.AddRange(_items).Count;

    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_LanguageExt_Seq_Concat() => (_lexSeq + Prelude.toSeq(_items)).Strict().Count;

    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_LanguageExt_Lst_AddRange() => _lexLst.AddRange(_items).Count;

    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_FSharpx_PersistentVector_PerEdit()
    {
        var v = _fx;
        foreach (var x in _items) v = v.Conj(x);
        return v.Length;
    }

#if DURABLE7
    [Benchmark, BenchmarkCategory("Batch append")]
    public int BatchAppend_Durable7_RrbVector_Builder()
    {
        var b = _d7.ToBuilder();
        b.AddRange(_items);
        return b.ToImmutable().Count;
    }
#endif
}

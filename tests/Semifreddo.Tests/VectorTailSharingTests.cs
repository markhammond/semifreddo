using System.Runtime.CompilerServices;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// The persistent vector's tail is shared: the first append to a version claims the next slot of its tail in place,
/// any later append to that version copies. Versions branch from anywhere, from many threads; none may see another's
/// elements, and references a version no longer holds go once it pops them or trims.
/// </summary>
public class VectorTailSharingTests
{
    private static void AssertEqual<T>(List<T> expected, ImmVector<T> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
        var j = 0;
        foreach (var x in actual) Assert.Equal(expected[j++], x);
        Assert.Equal(expected.Count, j);
        j = 0;
        foreach (var chunk in actual.Chunks)
            foreach (var x in chunk) Assert.Equal(expected[j++], x);
        Assert.Equal(expected.Count, j);
        if (expected.Count > 0) Assert.Equal(expected[^1], actual.Last);
    }

    [Fact]
    public void BranchingAppends_TheFirstClaimsTheTail_TheSecondCopies()
    {
        var v = ImmVector.Create<long>([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var a = v.Add(100);
        var b = v.Add(200);
        Assert.Same(v.Head!.Tail, a.Head!.Tail);
        Assert.NotSame(v.Head.Tail, b.Head!.Tail);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], v);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100], a);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 200], b);
    }

    [Fact]
    public void Appending_AllocatesAHeader_AndALeafPerTailFilled()
    {
        var bytes = Alloc.SteadyState(() =>
        {
            var v = ImmVector<long>.Empty;
            for (var i = 0; i < 32; i++) v = v.Add(i);
        });
        Assert.True(bytes <= 32 * 40 + 288, $"32 appends allocated {bytes} B");   // headers + the tail's leaf
    }

    [Fact]
    public void PopThenAppend_NeverOverwritesTheLongerVersion()
    {
        var v = ImmVector.Create<long>([0, 1, 2, 3, 4]);
        var w = v.RemoveLast();
        Assert.Same(v.Head!.Tail, w.Head!.Tail);              // no references in a long: pops share the tail
        var x = w.Add(99);
        Assert.NotSame(v.Head.Tail, x.Head!.Tail);
        AssertEqual([0, 1, 2, 3, 4], v);
        AssertEqual([0, 1, 2, 3, 99], x);
    }

    [Fact]
    public void Pops_OfReferences_CopyTheTail()
    {
        var v = ImmVector.Create<string>(["a", "b", "c"]);
        Assert.NotSame(v.Head!.Tail, v.RemoveLast().Head!.Tail);
        Assert.NotSame(v.Head.Tail, v.Take(2).Head!.Tail);
    }

    [Fact]
    public void RemoveLast_LetsThePoppedReferenceGo()
    {
        var (popped, gone) = PushAndPop();
        Collect();
        Assert.False(gone.IsAlive);
        Assert.Equal(5, popped.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ImmVector<object> Popped, WeakReference Gone) PushAndPop()
    {
        var v = ImmVector<object>.Empty;
        for (var i = 0; i < 5; i++) v = v.Add(new object());
        var last = new object();
        return (v.Add(last).RemoveLast(), new WeakReference(last));
    }

    [Fact]
    public void TrimExcess_ReleasesADescendantsAppends()
    {
        var (trimmed, appended) = TrimmedAncestor();
        Collect();
        Assert.False(appended.IsAlive);
        Assert.Equal(5, trimmed.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ImmVector<object> Trimmed, WeakReference Appended) TrimmedAncestor()
    {
        var (ancestor, appended) = AppendToADescendant();
        Collect();
        Assert.True(appended.IsAlive);                         // the ancestor's tail holds its descendant's append
        var trimmed = ancestor.TrimExcess();
        Assert.NotSame(ancestor.Head!.Tail, trimmed.Head!.Tail);
        return (trimmed, appended);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ImmVector<object> Ancestor, WeakReference Appended) AppendToADescendant()
    {
        var v = ImmVector<object>.Empty;
        for (var i = 0; i < 5; i++) v = v.Add(new object());
        var o = new object();
        _ = v.Add(o);
        return (v, new WeakReference(o));
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void TrimExcess_OfAVersionHoldingOnlyItsOwn_IsThatVersion()
    {
        var v = ImmVector.Create<string>(["a", "b", "c"]);
        Assert.True(v.TrimExcess().IsSameAs(v));
        Assert.True(ImmVector<string>.Empty.TrimExcess().IsEmpty);
        var full = ImmVector.Create<int>(Enumerable.Range(0, 64).ToArray());
        Assert.True(full.TrimExcess().IsSameAs(full));        // a full tail has nothing beyond
        var w = v.Add("d");
        Assert.False(v.TrimExcess().IsSameAs(v));
        AssertEqual(["a", "b", "c"], v.TrimExcess());
        AssertEqual(["a", "b", "c", "d"], w);
    }

    [Fact]
    public void ReadmeExample_TrimmedUndo_HoldsOnlyItsOwn()
    {
        var draft = ImmVector<string>.Empty.Add("whip the cream");
        var undo = draft;                            // kept for undo
        draft = draft.Add("fold in the meringue");   // written into the tail undo shares
        Assert.Same(undo.Head!.Tail, draft.Head!.Tail);
        undo = undo.TrimExcess();                    // undo's tail no longer holds the new step
        Assert.NotSame(undo.Head!.Tail, draft.Head!.Tail);
        Assert.Null(undo.Head.Tail.Items[1]);
        AssertEqual(["whip the cream"], undo);
        AssertEqual(["whip the cream", "fold in the meringue"], draft);
    }

    [Fact]
    public void ConcurrentAppends_ToOneVersion_OneClaims_EachKeepsItsOwn()
    {
        const int threads = 16;
        for (var round = 0; round < 200; round++)
        {
            var v = ImmVector.Create<long>(Enumerable.Range(0, 5 + round % 20).Select(i => (long)i).ToArray());
            var firsts = new ImmVector<long>[threads];
            var lasts = new ImmVector<long>[threads];
            using var start = new Barrier(threads);
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                start.SignalAndWait();
                var r = firsts[t] = v.Add(1000 + t);
                for (var k = 0; k < 40; k++) r = r.Add(100_000 * (t + 1) + k);
                lasts[t] = r;
            });
            Assert.Equal(1, firsts.Count(f => ReferenceEquals(f.Head!.Tail, v.Head!.Tail)));
            var model = v.ToArray().ToList();
            AssertEqual(model, v);
            for (var t = 0; t < threads; t++)
            {
                var m = new List<long>(model) { 1000 + t };
                AssertEqual(m, firsts[t]);
                for (var k = 0; k < 40; k++) m.Add(100_000 * (t + 1) + k);
                AssertEqual(m, lasts[t]);
            }
        }
    }

    [Theory]
    [InlineData(1, 20_000)]
    [InlineData(2, 20_000)]
    public void RandomBranching_Longs_EveryVersionMatchesItsList(int seed, int ops)
        => RandomBranching(seed, ops, static i => (long)i);

    [Theory]
    [InlineData(3, 20_000)]
    [InlineData(4, 20_000)]
    public void RandomBranching_Strings_EveryVersionMatchesItsList(int seed, int ops)
        => RandomBranching(seed, ops, static i => i.ToString());

    /// <summary>Every operation starts from a random version kept so far, so most branch from one already appended to.</summary>
    private static void RandomBranching<T>(int seed, int ops, Func<int, T> make)
    {
        var rnd = new Random(seed);
        var pool = new List<(ImmVector<T> Vector, List<T> Model)> { (ImmVector<T>.Empty, []) };
        for (var op = 0; op < ops; op++)
        {
            var (v, model) = pool[rnd.Next(pool.Count)];
            model = [.. model];
            var r = rnd.Next(100);
            if (r < 45 || model.Count == 0) { var x = make(op); v = v.Add(x); model.Add(x); }
            else if (r < 60) { v = v.RemoveLast(); model.RemoveAt(model.Count - 1); }
            else if (r < 70) { var i = rnd.Next(model.Count); var x = make(-op); v = v.SetItem(i, x); model[i] = x; }
            else if (r < 75) { var n = rnd.Next(model.Count + 1); v = v.Take(n); model.RemoveRange(n, model.Count - n); }
            else if (r < 80)
            {
                var items = Enumerable.Range(0, rnd.Next(80)).Select(k => make(op * 100 + k)).ToArray();
                v = v.AddRange(items); model.AddRange(items);
            }
            else if (r < 85)
            {
                var x = make(op);
                v = v.Mutate(x, static (s, x) => { s.Add(x); s.Add(x); }); model.Add(x); model.Add(x);
            }
            else if (r < 90) v = v.TrimExcess();
            else
            {
                for (var k = rnd.Next(1, 70); k > 0; k--) { var x = make(op * 1000 + k); v = v.Add(x); model.Add(x); }
            }
            if (pool.Count < 64) pool.Add((v, model));
            else pool[rnd.Next(pool.Count)] = (v, model);
            if (op % 1009 == 0) foreach (var (pv, pm) in pool) AssertEqual(pm, pv);
        }
        foreach (var (pv, pm) in pool) AssertEqual(pm, pv);
    }
}

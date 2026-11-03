using System.Runtime.CompilerServices;
using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// The RRB vector's tail is shared as <see cref="ImmVector{T}"/>'s is (see <see cref="VectorTailSharingTests"/>), and
/// joins the tree sealed, as it is, when the first version to flush it can: by appends, concatenation and inserts.
/// </summary>
public class FlexTailSharingTests
{
    private static void AssertEqual<T>(List<T> expected, ImmFlexVector<T> actual)
    {
        Check.FlexVector(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
        var j = 0;
        foreach (var x in actual) Assert.Equal(expected[j++], x);
        Assert.Equal(expected.Count, j);
        j = 0;
        foreach (var chunk in actual.Chunks)
            foreach (var x in chunk) Assert.Equal(expected[j++], x);
        Assert.Equal(expected.Count, j);
        Assert.Equal(expected, actual.ToList());                // the heap-safe cursor
    }

    [Fact]
    public void BranchingAppends_TheFirstClaimsTheTail_TheSecondCopies()
    {
        var v = ImmFlexVector.Create<long>([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var a = v.Add(100);
        var b = v.Add(200);
        Assert.Same(v.Head!.Tail, a.Head!.Tail);
        Assert.NotSame(v.Head.Tail, b.Head!.Tail);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], v);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 100], a);
        AssertEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 200], b);
    }

    [Fact]
    public void AFullTail_JoinsTheTreeAsItIs_ForEveryBranch()
    {
        var v = ImmFlexVector.Create<long>(Enumerable.Range(0, 32).Select(i => (long)i).ToArray());
        var tail = v.Head!.Tail;
        var a = v.Add(100);
        var b = v.Add(200);
        Assert.Same(tail, LastLeaf(a));
        Assert.Same(tail, LastLeaf(b));                         // sealed at that length: shared, not copied
        AssertEqual([.. Enumerable.Range(0, 32).Select(i => (long)i), 100], a);
        AssertEqual([.. Enumerable.Range(0, 32).Select(i => (long)i), 200], b);
    }

    private static object LastLeaf<T>(ImmFlexVector<T> v)
    {
        object n = v.Head!.Root!;
        while (n is Internal.FBranch<T> b) n = b.C[b.Len - 1]!;
        return n;
    }

    [Fact]
    public void Concatenation_SealsAPartialTail_AndSharesTheRightTail()
    {
        var a = ImmFlexVector.Create<long>([1, 2, 3]);
        var b = ImmFlexVector.Create<long>(Enumerable.Range(10, 40).Select(i => (long)i).ToArray());
        var ab = a + b;
        Assert.Same(b.Head!.Tail, ab.Head!.Tail);
        var a4 = a.Add(4);                                      // a's tail is sealed in ab's tree: a copies
        Assert.NotSame(a.Head!.Tail, a4.Head!.Tail);
        AssertEqual([1, 2, 3, .. Enumerable.Range(10, 40).Select(i => (long)i)], ab);
        AssertEqual([1, 2, 3, 4], a4);
        AssertEqual([1, 2, 3], a);
        var bb = b + b;                                         // b's tail both sealed into the tree and kept as the tail
        AssertEqual([.. Enumerable.Range(10, 40).Select(i => (long)i), .. Enumerable.Range(10, 40).Select(i => (long)i)], bb);
        AssertEqual([.. Enumerable.Range(10, 40).Select(i => (long)i), .. Enumerable.Range(10, 40).Select(i => (long)i), 7], bb.Add(7));
    }

    [Fact]
    public void Appending_AllocatesAHeader_AndATailPerTailFilled()
    {
        var bytes = Alloc.SteadyState(() =>
        {
            var v = ImmFlexVector<long>.Empty;
            for (var i = 0; i < 32; i++) v = v.Add(i);
        });
        Assert.True(bytes <= 32 * 56 + 296, $"32 appends allocated {bytes} B");   // headers + the tail
    }

    [Fact]
    public void PopThenAppend_NeverOverwritesTheLongerVersion()
    {
        var v = ImmFlexVector.Create<long>([0, 1, 2, 3, 4]);
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
        var v = ImmFlexVector.Create<string>(["a", "b", "c"]);
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
    private static (ImmFlexVector<object> Popped, WeakReference Gone) PushAndPop()
    {
        var v = ImmFlexVector<object>.Empty;
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
    private static (ImmFlexVector<object> Trimmed, WeakReference Appended) TrimmedAncestor()
    {
        var (ancestor, appended) = AppendToADescendant();
        Collect();
        Assert.True(appended.IsAlive);                         // the ancestor's tail holds its descendant's append
        var trimmed = ancestor.TrimExcess();
        Assert.NotSame(ancestor.Head!.Tail, trimmed.Head!.Tail);
        return (trimmed, appended);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ImmFlexVector<object> Ancestor, WeakReference Appended) AppendToADescendant()
    {
        var v = ImmFlexVector<object>.Empty;
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
        var v = ImmFlexVector.Create<string>(["a", "b", "c"]);
        Assert.True(v.TrimExcess().IsSameAs(v));
        Assert.True(ImmFlexVector<string>.Empty.TrimExcess().IsEmpty);
        var noTail = ImmFlexVector.Create<int>(Enumerable.Range(0, 64).ToArray()).Take(32);
        Assert.True(noTail.TrimExcess().IsSameAs(noTail));
        var w = v.Add("d");
        Assert.False(v.TrimExcess().IsSameAs(v));
        AssertEqual(["a", "b", "c"], v.TrimExcess());
        AssertEqual(["a", "b", "c", "d"], w);
    }

    [Fact]
    public void ConcurrentAppends_ToOneVersion_OneClaims_EachKeepsItsOwn()
    {
        const int threads = 16;
        for (var round = 0; round < 200; round++)
        {
            var v = ImmFlexVector<long>.Empty;                  // appended, so the tail is claimable
            for (var i = 0; i < 5 + round % 40; i++) v = v.Add(i);
            var firsts = new ImmFlexVector<long>[threads];
            var lasts = new ImmFlexVector<long>[threads];
            using var start = new Barrier(threads);
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                start.SignalAndWait();
                var r = firsts[t] = v.Add(1000 + t);
                for (var k = 0; k < 40; k++) r = r.Add(100_000 * (t + 1) + k);
                lasts[t] = r;
            });
            if (v.Head!.TailLen is > 0 and < 32)
                Assert.Equal(1, firsts.Count(f => ReferenceEquals(f.Head!.Tail, v.Head.Tail)));
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

    [Fact]
    public void ConcurrentConcatenations_OfOneVersion_EachKeepsItsOwn()
    {
        const int threads = 16;
        for (var round = 0; round < 200; round++)
        {
            var v = ImmFlexVector.Create<long>(Enumerable.Range(0, 1 + round % 70).Select(i => (long)i).ToArray());
            var right = ImmFlexVector.Create<long>(Enumerable.Range(500, 40).Select(i => (long)i).ToArray());
            var results = new ImmFlexVector<long>[threads];
            using var start = new Barrier(threads);
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                start.SignalAndWait();
                results[t] = t % 2 == 0 ? (v + right).Add(t) : v.Add(t).Add(-t);
            });
            var model = v.ToArray().ToList();
            AssertEqual(model, v);
            for (var t = 0; t < threads; t++)
                AssertEqual(t % 2 == 0 ? [.. model, .. Enumerable.Range(500, 40).Select(i => (long)i), t] : [.. model, t, -t], results[t]);
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
        var pool = new List<(ImmFlexVector<T> Vector, List<T> Model)> { (ImmFlexVector<T>.Empty, []) };
        for (var op = 0; op < ops; op++)
        {
            var (v, model) = pool[rnd.Next(pool.Count)];
            model = [.. model];
            var r = rnd.Next(100);
            if (r < 30 || model.Count == 0) { var x = make(op); v = v.Add(x); model.Add(x); }
            else if (r < 40) { v = v.RemoveLast(); model.RemoveAt(model.Count - 1); }
            else if (r < 46) { var i = rnd.Next(model.Count); var x = make(-op); v = v.SetItem(i, x); model[i] = x; }
            else if (r < 50) { var n = rnd.Next(model.Count + 1); v = v.Take(n); model.RemoveRange(n, model.Count - n); }
            else if (r < 54) { var n = rnd.Next(model.Count + 1); v = v.Skip(n); model.RemoveRange(0, n); }
            else if (r < 60)
            {
                var (w, wm) = pool[rnd.Next(pool.Count)];               // often a relative, or the same version
                v += w; model.AddRange(wm);
            }
            else if (r < 66) { var i = rnd.Next(model.Count + 1); var x = make(op); v = v.Insert(i, x); model.Insert(i, x); }
            else if (r < 71)
            {
                int i = rnd.Next(model.Count), n = rnd.Next(Math.Min(model.Count - i, 140) + 1);
                v = v.RemoveRange(i, n); model.RemoveRange(i, n);
            }
            else if (r < 75)
            {
                var items = Enumerable.Range(0, rnd.Next(80)).Select(k => make(op * 100 + k)).ToArray();
                v = v.AddRange(items); model.AddRange(items);
            }
            else if (r < 81)
            {
                var x = make(op);
                var at = rnd.Next(model.Count + 1);
                v = v.Mutate((x, at), static (s, a) =>
                {
                    s.Add(a.x);
                    s.Insert(a.at, a.x);
                    if (s.Count > 3) { s.RemoveAt(s.Count - 2); s.Take(s.Count - 1); }
                    s.Add(a.x);
                });
                model.Add(x);
                model.Insert(at, x);
                if (model.Count > 3) { model.RemoveAt(model.Count - 2); model.RemoveAt(model.Count - 1); }
                model.Add(x);
            }
            else if (r < 85) v = v.TrimExcess();
            else
            {
                for (var k = rnd.Next(1, 70); k > 0; k--) { var x = make(op * 1000 + k); v = v.Add(x); model.Add(x); }
            }
            if (model.Count > 3000) { v = v.Skip(model.Count - 1500); model.RemoveRange(0, model.Count - 1500); }
            if (pool.Count < 64) pool.Add((v, model));
            else pool[rnd.Next(pool.Count)] = (v, model);
            if (op % 1009 == 0) foreach (var (pv, pm) in pool) AssertEqual(pm, pv);
        }
        foreach (var (pv, pm) in pool) AssertEqual(pm, pv);
    }
}

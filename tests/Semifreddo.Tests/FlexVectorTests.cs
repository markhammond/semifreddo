using Semifreddo.Internal;
using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

public class FlexVectorTests
{
    // ------------------------------------------------------------------ structural invariants

    internal static void CheckInvariants<T>(ImmFlexVector<T> v) => Check.FlexVector(v);

    private static void AssertEqual<T>(List<T> expected, ImmFlexVector<T> actual, Random? sample = null)
    {
        CheckInvariants(actual);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual.ToArray());
        var j = 0;
        foreach (var x in actual) Assert.Equal(expected[j++], x);
        Assert.Equal(expected.Count, j);
        if (sample is null) for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
        else for (var k = 0; k < 200 && expected.Count > 0; k++) { var i = sample.Next(expected.Count); Assert.Equal(expected[i], actual[i]); }
    }

    private static (ImmFlexVector<int>, List<int>) RandomVector(Random rnd, int max, ref int seq)
    {
        var n = rnd.Next(max);
        var list = new List<int>(n);
        for (var i = 0; i < n; i++) list.Add(seq++);
        ImmFlexVector<int> v;
        switch (rnd.Next(3))
        {
            case 0: v = ImmFlexVector.Create<int>(list.ToArray()); break;
            case 1: v = default; foreach (var x in list) v = v.Add(x); break;
            default: // built by concatenating random pieces => relaxed nodes
                v = default; var k = 0;
                while (k < n) { var m = Math.Min(n - k, 1 + rnd.Next(70)); v += ImmFlexVector.Create<int>(list.GetRange(k, m).ToArray()); k += m; }
                break;
        }
        return (v, list);
    }

    // ------------------------------------------------------------------ tests

    [Theory]
    [InlineData(1, 4000)]
    [InlineData(2, 4000)]
    [InlineData(3, 1500)]
    public void Persistent_RandomOps_IncludingConcatAndSplit(int seed, int ops)
    {
        var rnd = new Random(seed);
        var seq = 0;
        var v = ImmFlexVector<int>.Empty;
        var model = new List<int>();
        var history = new List<(ImmFlexVector<int>, List<int>)>();

        for (var op = 0; op < ops; op++)
        {
            var r = rnd.Next(100);
            var c = model.Count;
            if (r < 20) { v = v.Add(seq); model.Add(seq++); }
            else if (r < 28) { v = v.Prepend(seq); model.Insert(0, seq++); }
            else if (r < 38) { var i = rnd.Next(c + 1); v = v.Insert(i, seq); model.Insert(i, seq++); }
            else if (r < 46 && c > 0) { var i = rnd.Next(c); v = v.RemoveAt(i); model.RemoveAt(i); }
            else if (r < 52 && c > 0) { var i = rnd.Next(c); var n = rnd.Next(c - i + 1); v = v.RemoveRange(i, n); model.RemoveRange(i, n); }
            else if (r < 60 && c > 0) { var i = rnd.Next(c); v = v.SetItem(i, -op); model[i] = -op; }
            else if (r < 66) { var n = rnd.Next(c + 1); v = v.Take(n); model.RemoveRange(n, c - n); }
            else if (r < 72) { var n = rnd.Next(c + 1); v = v.Skip(n); model.RemoveRange(0, n); }
            else if (r < 76) { int s = rnd.Next(c + 1), n = rnd.Next(c - s + 1); v = v.Slice(s, n); model = model.GetRange(s, n); }
            else if (r < 90) { var (o, ol) = RandomVector(rnd, 3000, ref seq); if (rnd.Next(2) == 0) { v += o; model.AddRange(ol); } else { v = o + v; model.InsertRange(0, ol); } }
            else if (r < 95) { var i = rnd.Next(c + 1); var (o, ol) = RandomVector(rnd, 500, ref seq); v = v.InsertRange(i, o); model.InsertRange(i, ol); }
            else { v = v + v; model.AddRange(model.ToList()); if (model.Count > 200_000) { v = v.Take(1000); model.RemoveRange(1000, model.Count - 1000); } }

            if (op % 50 == 0) { AssertEqual(model, v, rnd); history.Add((v, [.. model])); }
            else CheckInvariants(v);
        }
        AssertEqual(model, v);
        foreach (var (hv, hm) in history) AssertEqual(hm, hv, rnd);
    }

    [Fact]
    public void ManySmallConcats_StayShallowAndIndexable()
    {
        var v = ImmFlexVector<int>.Empty;
        var model = new List<int>();
        var rnd = new Random(9);
        for (var i = 0; i < 20_000; i++)
        {
            var n = 1 + rnd.Next(5);
            var piece = Enumerable.Range(model.Count, n).ToArray();
            v += ImmFlexVector.Create<int>(piece);
            model.AddRange(piece);
        }
        AssertEqual(model, v);
    }

    [Fact]
    public void LargeConcat_OfDenseHalves()
    {
        var a = ImmFlexVector.Create<int>(Enumerable.Range(0, 100_003).ToArray());
        var b = ImmFlexVector.Create<int>(Enumerable.Range(100_003, 77_777).ToArray());
        var c = a + b;
        AssertEqual(Enumerable.Range(0, 177_780).ToList(), c);
        AssertEqual(Enumerable.Range(0, 100_003).ToList(), a); // inputs untouched
        var mid = c.Slice(50_000, 100_000);
        AssertEqual(Enumerable.Range(50_000, 100_000).ToList(), mid);
    }

    [Theory]
    [InlineData(4, 4000)]
    [InlineData(5, 4000)]
    public void Transient_RandomOps_SnapshotsAreIsolated(int seed, int ops)
    {
        var rnd = new Random(seed);
        var seq = 0;
        var (start, model) = RandomVector(rnd, 5000, ref seq);
        var t = start.ToTransient();
        var startModel = model.ToList();
        var history = new List<(ImmFlexVector<int>, List<int>)>();
        for (var op = 0; op < ops; op++)
        {
            int r = rnd.Next(100), c = model.Count;
            if (r < 30) { t.Add(seq); model.Add(seq++); }
            else if (r < 45 && c > 0) { var i = rnd.Next(c); t.ItemRef(i) += 1; model[i] += 1; }
            else if (r < 55) { var i = rnd.Next(c + 1); t.Insert(i, seq); model.Insert(i, seq++); }
            else if (r < 63 && c > 0) { var i = rnd.Next(c); t.RemoveAt(i); model.RemoveAt(i); }
            else if (r < 68) { var n = rnd.Next(c + 1); t.Take(n); model.RemoveRange(n, c - n); }
            else if (r < 73) { var n = rnd.Next(c + 1); t.Skip(n); model.RemoveRange(0, n); }
            else if (r < 80) { var (o, ol) = RandomVector(rnd, 800, ref seq); t.AddRange(o); model.AddRange(ol); }
            else if (r < 90) { var arr = Enumerable.Range(seq, rnd.Next(100)).ToArray(); seq += arr.Length; t.AddRange(arr); model.AddRange(arr); }
            else history.Add((t.ToPersistent(), [.. model]));
            Assert.Equal(model.Count, t.Count);
        }
        AssertEqual(model, t.ToPersistent());
        AssertEqual(startModel, start);
        foreach (var (hv, hm) in history) AssertEqual(hm, hv, rnd);
    }

    [Fact]
    public void Transient_Writes_AreAllocationFree_OnceOwned()
    {
        var t = ImmFlexVector.CreateTransient<long>();
        for (var i = 0; i < 64_000; i++) t.Add(i);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 31; i++) t.Add(i); // count 64000 = 2000 * 32: one leaf flush, then tail writes
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 400);
        Assert.Equal(0, Alloc.SteadyState(() => { for (var i = 0; i < 64_000; i++) t.ItemRef(i) += 1; }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomInserts_KeepLeavesFull_AndMatchModel(bool transient)
    {
        var rnd = new Random(transient ? 21 : 22);
        var model = Enumerable.Range(0, 40_000).ToList();
        var v = ImmFlexVector.Create<int>(model.ToArray());
        var t = transient ? v.ToTransient() : null;
        for (var k = 0; k < 30_000; k++)
        {
            var i = rnd.Next(model.Count + 1);
            model.Insert(i, -k);
            if (t is null) v = v.Insert(i, -k); else t.Insert(i, -k);
            if (k % 7_500 == 0) AssertEqual(model, t?.ToPersistent() ?? v, rnd);
        }
        v = t?.ToPersistent() ?? v;
        AssertEqual(model, v);
        long leaves = 0, fill = 0;
        void Walk(FNode<int> n, int shift)
        {
            if (shift == 0) { leaves++; fill += n.Count; return; }
            var b = FTree<int>.AsBranch(n);
            for (var j = 0; j < b.Len; j++) Walk(b.C[j]!, shift - 5);
        }
        Walk(v.Head!.Root!, v.Head.Shift);
        Assert.True(fill / (double)leaves > 26, $"avg leaf fill {fill / (double)leaves:F1}"); // plain 17/16 splitting gives ~22
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RandomRemovals_DirectPath_MatchModel_AndShrinkTree(bool transient)
    {
        var rnd = new Random(transient ? 31 : 32);
        var model = Enumerable.Range(0, 60_000).ToList();
        var v = ImmFlexVector.Create<int>(model.ToArray());
        // relax the tree first so removals hit relaxed nodes too
        for (var k = 0; k < 2_000; k++) { var i = rnd.Next(model.Count); model.Insert(i, -k); v = v.Insert(i, -k); }
        var start = v;
        var startModel = model.ToList();
        var t = transient ? v.ToTransient() : null;
        var history = new List<(ImmFlexVector<int>, List<int>)>();
        while (model.Count > 500)
        {
            int i = rnd.Next(model.Count), r = rnd.Next(10);
            var n = r < 6 ? 1 : r < 9 ? 1 + rnd.Next(16) : 1 + rnd.Next(80); // direct path, direct loop, split+concat
            n = Math.Min(n, model.Count - i);
            model.RemoveRange(i, n);
            if (t is null) v = v.RemoveRange(i, n); else t.RemoveRange(i, n);
            if (rnd.Next(400) == 0) { var snap = t?.ToPersistent() ?? v; AssertEqual(model, snap, rnd); history.Add((snap, model.ToList())); }
        }
        v = t?.ToPersistent() ?? v;
        AssertEqual(model, v);
        AssertEqual(startModel, start, rnd);
        foreach (var (hv, hm) in history) AssertEqual(hm, hv, rnd);
        Assert.True(v.Head!.Shift <= 10, $"height not shrunk: shift {v.Head.Shift}"); // 500 elements fit in height <= 2
        long leaves = 0, fill = 0;
        void Walk(FNode<int> n, int shift)
        {
            if (shift == 0) { leaves++; fill += n.Count; return; }
            var b = FTree<int>.AsBranch(n);
            for (var j = 0; j < b.Len; j++) Walk(b.C[j]!, shift - 5);
        }
        if (v.Head.Root is { } root) Walk(root, v.Head.Shift);
        Assert.True(leaves == 0 || fill / (double)leaves >= 8, $"avg leaf fill {fill / (double)leaves:F1}");
    }

    [Fact]
    public void Transient_RemoveAt_IsNearlyAllocationFree_OnceOwned()
    {
        var t = ImmFlexVector.Create<long>(Enumerable.Range(0, 200_000).Select(i => (long)i).ToArray()).ToTransient();
        var rnd = new Random(5);
        for (var k = 0; k < 2_000; k++) t.RemoveAt(rnd.Next(t.Count));            // first touch: paths become owned
        for (var i = 0; i < t.Count; i += 1) t.ItemRef(i) += 0;                    // own every path
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var k = 0; k < 10_000; k++) t.RemoveAt(rnd.Next(t.Count));
        var perOp = (GC.GetAllocatedBytesForCurrentThread() - before) / 10_000;
        Assert.True(perOp < 64, $"{perOp} B per transient RemoveAt"); // split + concat cost ~6.6 KB per call
    }

    [Fact]
    public void RangeIndexer_And_CollectionExpression()
    {
        ImmFlexVector<int> v = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal([2, 3, 4], v[2..5].ToArray());
        Assert.Equal([7, 8, 9], v[^3..].ToArray());
    }
}

using Xunit;

namespace Semifreddo.Tests;

public class VectorTests
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
        Assert.Equal(expected, actual.ToArray());
    }

    [Theory]
    [InlineData(1, 3000)]
    [InlineData(2, 40000)]
    public void Persistent_RandomOps_MatchListAndPreserveHistory(int seed, int ops)
    {
        var rnd = new Random(seed);
        var v = ImmVector<string>.Empty;
        var model = new List<string>();
        var history = new List<(ImmVector<string>, List<string>)>();

        for (var op = 0; op < ops; op++)
        {
            var r = rnd.Next(100);
            if (r < 60 || model.Count == 0) { var s = op.ToString(); v = v.Add(s); model.Add(s); }
            else if (r < 75) { var i = rnd.Next(model.Count); v = v.SetItem(i, "s" + op); model[i] = "s" + op; }
            else if (r < 88) { v = v.RemoveLast(); model.RemoveAt(model.Count - 1); }
            else if (r < 92) { var n = rnd.Next(model.Count + 1); v = v.Take(n); model.RemoveRange(n, model.Count - n); }
            else
            {
                var arr = Enumerable.Range(0, rnd.Next(200)).Select(x => $"r{op}_{x}").ToArray();
                v = v.AddRange(arr); model.AddRange(arr);
            }
            if (op % 97 == 0) history.Add((v, [.. model]));
        }
        AssertEqual(model, v);
        foreach (var (hv, hm) in history) AssertEqual(hm, hv);
    }

    [Fact]
    public void Large_AddRange_And_Pop_CrossesLevels()
    {
        const int n = 32 * 32 * 32 * 2 + 77; // shift 15
        var src = Enumerable.Range(0, n).ToArray();
        var v = ImmVector.Create<int>(src);
        AssertEqual([.. src], v);
        var model = src.ToList();
        for (var k = 0; k < n; k += 1)
        {
            v = v.RemoveLast(); model.RemoveAt(model.Count - 1);
            if (k % 4099 == 0) AssertEqual(model, v);
        }
        Assert.True(v.IsEmpty);
    }

    [Theory]
    [InlineData(3, 60000)]
    [InlineData(4, 5000)]
    public void Transient_RandomOps_SnapshotsAreIsolated(int seed, int ops)
    {
        var rnd = new Random(seed);
        var t = ImmVector.CreateTransient<int>();
        var model = new List<int>();
        var history = new List<(ImmVector<int>, List<int>)>();

        for (var op = 0; op < ops; op++)
        {
            var r = rnd.Next(100);
            if (r < 55 || model.Count == 0) { t.Add(op); model.Add(op); }
            else if (r < 75) { var i = rnd.Next(model.Count); t.ItemRef(i) += 7; model[i] += 7; }
            else if (r < 88) { t.RemoveLast(); model.RemoveAt(model.Count - 1); }
            else if (r < 90) { var n = rnd.Next(model.Count + 1); t.Take(n); model.RemoveRange(n, model.Count - n); }
            else if (r < 96)
            {
                var arr = Enumerable.Range(op, rnd.Next(300)).ToArray();
                t.AddRange(arr); model.AddRange(arr);
            }
            else history.Add((t.ToPersistent(), [.. model]));
            Assert.Equal(model.Count, t.Count);
        }
        AssertEqual(model, t.ToPersistent());
        foreach (var (hv, hm) in history) AssertEqual(hm, hv);
    }

    [Fact]
    public void Transient_FromPersistent_DoesNotMutateSource()
    {
        var src = Enumerable.Range(0, 5000).ToArray();
        var v = ImmVector.Create<int>(src);
        var t = v.ToTransient();
        for (var i = 0; i < 5000; i++) t[i] = -i;
        for (var i = 0; i < 2000; i++) t.RemoveLast();
        t.Add(42);
        AssertEqual([.. src], v);
        var w = t.ToPersistent();
        Assert.Equal(3001, w.Count);
        Assert.Equal(-2999, w[2999]);
        Assert.Equal(42, w[3000]);
    }

    [Fact]
    public void Mutate_NoChanges_ReturnsSameInstance()
    {
        ImmVector<int> v = [1, 2, 3];
        var w = v.Mutate(static t => { _ = t.Count; });
        Assert.True(v.IsSameAs(w));
    }

    [Fact]
    public void Transient_Add_IsAllocationFree_BetweenLeaves()
    {
        var t = ImmVector.CreateTransient<long>();
        for (var i = 0; i < 100_000; i++) t.Add(i); // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 31; i++) t.Add(i); // count was 100000 (multiple of 32): first add flushes one leaf
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 400, $"allocated {bytes}"); // one 32 x long leaf ~ 300B (+ maybe nothing else)

        Assert.Equal(0, Alloc.SteadyState(() => { for (var i = 0; i < 100_000; i++) t.ItemRef(i) += 1; })); // tree owned
    }

    [Fact]
    public void Enumerators_AreAllocationFree()
    {
        var v = ImmVector.Create<int>(Enumerable.Range(0, 10_000).ToArray());
        long sum = 0;
        Assert.Equal(0, Alloc.SteadyState(() =>
        {
            sum = 0;
            foreach (var x in v) sum += x;
            foreach (var c in v.Chunks) foreach (var x in c) sum += x;
        }));
        Assert.Equal(2L * 9999 * 10000 / 2, sum);
    }

    [Fact]
    public void Interlocked_ConcurrentAppends_AreNotLost()
    {
        var shared = ImmVector<int>.Empty;
        Parallel.For(0, 8, w =>
        {
            for (var i = 0; i < 5000; i++) ImmInterlocked.Update(ref shared, static (v, x) => v.Add(x), i);
        });
        Assert.Equal(40000, shared.Count);
    }
}

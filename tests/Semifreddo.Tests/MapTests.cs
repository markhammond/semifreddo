using Xunit;

namespace Semifreddo.Tests;

/// <summary>Key with a deliberately terrible hash to force deep paths and collision nodes.</summary>
public readonly record struct BadKey(int Id)
{
    public override int GetHashCode() => Id & 3;
}

public class MapTests
{
    private static void AssertEqual<K, V>(Dictionary<K, V> expected, ImmMap<K, V> actual) where K : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (k, v) in expected)
        {
            Assert.True(actual.TryGetValue(k, out var a), $"missing {k}");
            Assert.Equal(v, a);
        }
        var n = 0;
        foreach (var kv in actual) { Assert.Equal(expected[kv.Key], kv.Value); n++; }
        Assert.Equal(expected.Count, n);
    }

    private static void RunPersistent<K>(Func<int, K> key, int seed, int ops, int keySpace) where K : notnull
    {
        var rnd = new Random(seed);
        var m = ImmMap<K, int>.Empty;
        var model = new Dictionary<K, int>();
        var history = new List<(ImmMap<K, int>, Dictionary<K, int>)>();
        for (var op = 0; op < ops; op++)
        {
            var k = key(rnd.Next(keySpace));
            if (rnd.Next(100) < 60) { m = m.SetItem(k, op); model[k] = op; }
            else
            {
                var before = m;
                m = m.Remove(k);
                if (!model.Remove(k)) Assert.True(before.IsSameAs(m));
            }
            Assert.False(m.ContainsKey(key(-1)));
            if (op % 101 == 0) history.Add((m, new(model)));
        }
        AssertEqual(model, m);
        foreach (var (hm, hd) in history) AssertEqual(hd, hm);
    }

    [Fact] public void Persistent_IntKeys() => RunPersistent(i => i, 1, 30000, 5000);
    [Fact] public void Persistent_StringKeys() => RunPersistent(i => "k" + i, 2, 20000, 3000);
    [Fact] public void Persistent_CollidingKeys() => RunPersistent(i => new BadKey(i), 3, 8000, 300);

    private static void RunTransient<K>(Func<int, K> key, int seed, int ops, int keySpace) where K : notnull
    {
        var rnd = new Random(seed);
        var t = ImmMap<K, long>.Empty.SetItem(key(0), 1).ToTransient();
        var model = new Dictionary<K, long> { [key(0)] = 1 };
        var history = new List<(ImmMap<K, long>, Dictionary<K, long>)>();
        for (var op = 0; op < ops; op++)
        {
            var k = key(rnd.Next(keySpace));
            var r = rnd.Next(100);
            if (r < 40) { t.GetValueRefOrAddDefault(k, out _) += op; model[k] = model.GetValueOrDefault(k) + op; }
            else if (r < 60) { t[k] = op; model[k] = op; }
            else if (r < 97) Assert.Equal(model.Remove(k), t.Remove(k));
            else history.Add((t.ToPersistent(), new(model)));
            Assert.Equal(model.Count, t.Count);
        }
        AssertEqual(model, t.ToPersistent());
        foreach (var (hm, hd) in history) AssertEqual(hd, hm);
    }

    [Fact] public void Transient_IntKeys() => RunTransient(i => i, 4, 50000, 4000);
    [Fact] public void Transient_StringKeys() => RunTransient(i => "k" + i, 5, 20000, 2000);
    [Fact] public void Transient_CollidingKeys() => RunTransient(i => new BadKey(i), 6, 8000, 200);

    [Fact]
    public void Transient_RefUpsert_IsAllocationFree_OnceOwned()
    {
        var t = ImmMap.CreateTransient<int, double>();
        for (var i = 0; i < 10_000; i++) t.GetValueRefOrAddDefault(i, out _) += 1;
        Assert.Equal(0, Alloc.SteadyState(() => { for (var i = 0; i < 10_000; i++) t.GetValueRefOrAddDefault(i, out _) += 1; }, warmup: 4, trials: 5));
        var m = t.ToPersistent();
        Assert.Equal(10.0, m[1234]);
        // after freezing, the transient copies on write and the snapshot is untouched
        t.GetValueRefOrAddDefault(1234, out _) = -1;
        Assert.Equal(10.0, m[1234]);
        Assert.Equal(-1.0, t.ToPersistent()[1234]);
    }

    [Fact]
    public void Lookup_And_Enumeration_AreAllocationFree()
    {
        var m = ImmMap.CreateRange(Enumerable.Range(0, 5000).Select(i => KeyValuePair.Create(i, (long)i)));
        long s = 0;
        Assert.Equal(0, Alloc.SteadyState(() =>
        {
            s = 0;
            for (var i = 0; i < 5000; i++) if (m.TryGetValue(i, out var v)) s += v;
            foreach (var kv in m) s += kv.Value;
        }));
        Assert.Equal(2L * 4999 * 5000 / 2, s);
    }

    [Fact]
    public void RemovingEverything_YieldsEmptyAndCanonicalShape()
    {
        var keys = Enumerable.Range(0, 3000).Select(i => new BadKey(i * 7)).ToArray();
        var full = ImmMap<BadKey, int>.Empty;
        foreach (var k in keys) full = full.SetItem(k, k.Id);
        var half = full;
        foreach (var k in keys.Where(k => k.Id % 2 == 0)) half = half.Remove(k);
        var fresh = ImmMap<BadKey, int>.Empty;
        foreach (var k in keys.Where(k => k.Id % 2 != 0)) fresh = fresh.SetItem(k, k.Id);
        Assert.True(SameShape(half.Root, fresh.Root));
        foreach (var k in keys) full = full.Remove(k);
        Assert.True(full.IsEmpty);
    }

    private static bool SameShape<TE>(Internal.ChampNode<TE>? a, Internal.ChampNode<TE>? b)
    {
        if (a is null || b is null) return a == b;
        if (a.DataMap != b.DataMap || a.NodeMap != b.NodeMap || a.Data.Length != b.Data.Length || a.Nodes.Length != b.Nodes.Length)
            return false;
        for (var i = 0; i < a.Nodes.Length; i++) if (!SameShape(a.Nodes[i], b.Nodes[i])) return false;
        return true;
    }

    [Fact]
    public void Interlocked_ConcurrentCounters()
    {
        var shared = ImmMap<int, int>.Empty;
        Parallel.For(0, 8, w =>
        {
            for (var i = 0; i < 2000; i++)
                ImmInterlocked.Update(ref shared, static (m, k) => m.SetItem(k, m.GetValueOrDefault(k) + 1), i % 50);
        });
        Assert.Equal(50, shared.Count);
        Assert.Equal(16000, shared.Sum(kv => kv.Value));
    }
}

public class SetTests
{
    [Fact]
    public void RandomOps_MatchHashSet()
    {
        var rnd = new Random(7);
        var s = ImmSet<BadKey>.Empty;
        var t = ImmSet.CreateTransient<BadKey>();
        var model = new HashSet<BadKey>();
        for (var op = 0; op < 20000; op++)
        {
            var k = new BadKey(rnd.Next(500));
            if (rnd.Next(2) == 0) { s = s.Add(k); Assert.Equal(model.Add(k), t.Add(k)); }
            else { s = s.Remove(k); Assert.Equal(model.Remove(k), t.Remove(k)); }
        }
        Assert.Equal(model.Count, s.Count);
        Assert.True(model.SetEquals(s));
        Assert.True(model.SetEquals(t.ToPersistent()));
    }

    [Fact]
    public void Algebra()
    {
        ImmSet<int> a = [1, 2, 3, 4];
        ImmSet<int> b = [3, 4, 5];
        Assert.True(new HashSet<int> { 1, 2, 3, 4, 5 }.SetEquals(a.Union(b)));
        Assert.True(new HashSet<int> { 1, 2 }.SetEquals(a.Except(b)));
        Assert.True(a.Add(1).IsSameAs(a));
    }
}

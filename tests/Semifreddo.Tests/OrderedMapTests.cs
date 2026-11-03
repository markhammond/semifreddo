using Xunit;

namespace Semifreddo.Tests;

public class OrderedMapTests
{
    private static void AssertEqual<K, V>(OrderedDictionary<K, V> expected, ImmOrderedMap<K, V> actual) where K : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.ToList(), actual.ToList()); // order matters
        foreach (var (k, v) in expected) Assert.Equal(v, actual[k]);
        if (expected.Count > 0)
        {
            Assert.Equal(expected.GetAt(0), actual.First);
            Assert.Equal(expected.GetAt(expected.Count - 1), actual.Last);
        }
        var h = actual.Head;
        if (h is not null)
        {
            Assert.True(h.Slots.First.Live && h.Slots.Last.Live);
            Assert.Equal(h.Slots.Count - h.Index.Count, h.Dead);
            FlexVectorTests.CheckInvariants(h.Slots);
        }
    }

    [Theory]
    [InlineData(1, 20000, 500)]
    [InlineData(2, 20000, 5000)]
    public void Persistent_RandomOps_MatchOrderedDictionary(int seed, int ops, int keySpace)
    {
        var rnd = new Random(seed);
        var m = ImmOrderedMap<int, int>.Empty;
        var model = new OrderedDictionary<int, int>();
        var history = new List<(ImmOrderedMap<int, int>, OrderedDictionary<int, int>)>();
        for (var op = 0; op < ops; op++)
        {
            int k = rnd.Next(keySpace), r = rnd.Next(100);
            if (r < 50) { m = m.SetItem(k, op); model[k] = op; }
            else if (r < 60 && model.Count > 0) { var f = model.GetAt(0).Key; m = m.Remove(f); model.Remove(f); } // FIFO pop
            else { m = m.Remove(k); model.Remove(k); }
            if (op % 211 == 0) { AssertEqual(model, m); history.Add((m, new(model))); }
        }
        AssertEqual(model, m);
        foreach (var (hm, hd) in history) AssertEqual(hd, hm);
    }

    [Theory]
    [InlineData(3, 30000, 2000)]
    [InlineData(4, 30000, 200)]
    public void Transient_RandomOps_MatchOrderedDictionary(int seed, int ops, int keySpace)
    {
        var rnd = new Random(seed);
        var t = ImmOrderedMap.CreateTransient<int, long>();
        var model = new OrderedDictionary<int, long>();
        var history = new List<(ImmOrderedMap<int, long>, OrderedDictionary<int, long>)>();
        for (var op = 0; op < ops; op++)
        {
            int k = rnd.Next(keySpace), r = rnd.Next(100);
            if (r < 30) { t.GetValueRefOrAddDefault(k, out _) += op; model[k] = model.GetValueOrDefault(k) + op; }
            else if (r < 50) { t[k] = op; model[k] = op; }
            else if (r < 60 && model.Count > 0) { var f = model.GetAt(0).Key; Assert.True(t.Remove(f)); model.Remove(f); }
            else if (r < 98) Assert.Equal(model.Remove(k), t.Remove(k));
            else history.Add((t.ToPersistent(), new(model)));
            Assert.Equal(model.Count, t.Count);
        }
        AssertEqual(model, t.ToPersistent());
        foreach (var (hm, hd) in history) AssertEqual(hd, hm);
    }

    [Fact]
    public void InteriorRemovals_Compact()
    {
        var m = ImmOrderedMap.CreateRange(Enumerable.Range(0, 1000).Select(i => KeyValuePair.Create(i, i)));
        for (var i = 1; i < 999; i++) m = m.Remove(i);
        Assert.Equal(2, m.Count);
        Assert.True(m.Head!.Slots.Count < 40, $"slots {m.Head.Slots.Count}"); // compacted, not 1000 slots
        Assert.Equal([0, 999], m.Keys.ToArray());
    }

    [Fact]
    public void Fifo_Queue_KeepsNoTombstones()
    {
        var m = ImmOrderedMap<long, int>.Empty;
        for (long id = 0; id < 50_000; id++)
        {
            m = m.SetItem(id, 1);
            if (id % 3 != 0) m = m.Remove(m.First.Key);
        }
        Assert.Equal(0, m.Head!.Dead);
        Assert.Equal(m.Count, m.Head.Slots.Count);
    }
}

public class ChampSpareCapacityTests
{
    private static void AssertExact<TE>(Internal.ChampNode<TE> n, int shift)
    {
        Assert.False(n.Slack);
        if (shift < 35)
        {
            Assert.Equal(System.Numerics.BitOperations.PopCount(n.DataMap), n.Data.Length);
            Assert.Equal(System.Numerics.BitOperations.PopCount(n.NodeMap), n.Nodes.Length);
        }
        foreach (var c in n.Nodes) AssertExact(c, shift + 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frozen_Nodes_AreExactSize_AndCanonical(bool collide)
    {
        var rnd = new Random(11);
        var keys = Enumerable.Range(0, 20_000).Select(i => new BadKey(collide ? i : i * 1_000_003)).ToArray();
        var t = ImmMap.CreateTransient<BadKey, int>();
        foreach (var k in keys) t[k] = k.Id;
        foreach (var k in keys.Where(_ => rnd.Next(2) == 0)) t.Remove(k);
        var m = t.ToPersistent();
        AssertExact(m.Root!, 0);
        var fresh = ImmMap<BadKey, int>.Empty;
        foreach (var kv in m) fresh = fresh.SetItem(kv.Key, kv.Value);
        Assert.Equal(fresh.Count, m.Count);
        Assert.True(SameShape(fresh.Root, m.Root));

        // keep using the transient after freezing: the snapshot must not change
        var snapshot = m.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var k in keys.Take(5000)) { t.Remove(k); t[new BadKey(-k.Id - 1)] = 1; }
        Assert.Equal(snapshot.Count, m.Count);
        foreach (var (k, v) in snapshot) Assert.Equal(v, m[k]);
        AssertExact(t.ToPersistent().Root!, 0);
    }

    private static bool SameShape<TE>(Internal.ChampNode<TE>? a, Internal.ChampNode<TE>? b)
    {
        if (a is null || b is null) return a == b;
        if (a.DataMap != b.DataMap || a.NodeMap != b.NodeMap || a.Data.Length != b.Data.Length || a.Nodes.Length != b.Nodes.Length) return false;
        for (var i = 0; i < a.Nodes.Length; i++) if (!SameShape(a.Nodes[i], b.Nodes[i])) return false;
        return true;
    }
}

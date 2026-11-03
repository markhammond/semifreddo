using Xunit;

namespace Semifreddo.Tests;

public class AlternateLookupTests
{
    private static readonly string[] Symbols = Enumerable.Range(0, 3000).Select(i => $"SYM{i:D5}").ToArray();

    [Fact]
    public void Map_SpanLookups_MatchStringLookups()
    {
        var rnd = new Random(1);
        var m = ImmMap<string, int>.Empty;
        var model = new Dictionary<string, int>();
        Span<char> buf = stackalloc char[16];
        for (var op = 0; op < 20_000; op++)
        {
            var s = Symbols[rnd.Next(Symbols.Length)];
            s.AsSpan().CopyTo(buf);
            ReadOnlySpan<char> key = buf[..s.Length];
            var alt = m.GetAlternateLookup<ReadOnlySpan<char>>();
            Assert.Equal(model.TryGetValue(s, out var mv), alt.TryGetValue(key, out var av));
            if (model.ContainsKey(s)) Assert.Equal(mv, av);
            if (rnd.Next(3) == 0) { m = alt.Remove(key); model.Remove(s); }
            else { m = m.SetItem(s, op); model[s] = op; }
        }
        Assert.Equal(model.Count, m.Count);
        var lookup = m.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var (k, v) in model)
        {
            Assert.True(lookup.TryGetValue(k.AsSpan(), out var actualKey, out var value));
            Assert.Same(k, actualKey);
            Assert.Equal(v, value);
        }
    }

    [Fact]
    public void Transient_SpanUpsert_AllocatesOnlyForNewKeys()
    {
        var t = ImmMap.CreateTransient<string, long>();
        var alt = t.GetAlternateLookup<ReadOnlySpan<char>>();
        var wire = string.Concat(Symbols).ToCharArray(); // simulated feed buffer: no string per message
        for (var i = 0; i < Symbols.Length; i++) alt.GetValueRefOrAddDefault(wire.AsSpan(i * 8, 8), out _) += 1;
        Assert.Equal(Symbols.Length, t.Count);

        Assert.Equal(0, Alloc.SteadyState(() =>
        {
            var a = t.GetAlternateLookup<ReadOnlySpan<char>>(); // ref struct: re-created inside the delegate
            for (var i = 0; i < Symbols.Length; i++) a.GetValueRefOrAddDefault(wire.AsSpan(i * 8, 8), out _) += 1;
        }, warmup: 5, trials: 5));

        var m = t.ToPersistent();
        Assert.Equal(11, m["SYM01234"]);
        Assert.True(alt.Remove("SYM01234".AsSpan()));
        Assert.False(t.ContainsKey("SYM01234"));
        Assert.True(m.ContainsKey("SYM01234")); // snapshot unaffected
    }

    [Fact]
    public void OrderedMap_SpanUpsert_SharesKeyInstance_AndKeepsOrder()
    {
        var om = ImmOrderedMap<string, int>.Empty.Mutate(static s =>
        {
            var alt = s.GetAlternateLookup<ReadOnlySpan<char>>();
            foreach (var name in (ReadOnlySpan<string>)["c", "a", "b"]) alt.GetValueRefOrAddDefault(name.AsSpan(), out _) = name[0];
            alt["a".AsSpan()] += 100;
            Assert.True(alt.Remove("c".AsSpan()));
        });
        Assert.Equal(["a", "b"], om.Keys.ToArray());
        Assert.Equal('a' + 100, om["a"]);

        var h = om.Head!;
        var lookup = om.GetAlternateLookup<ReadOnlySpan<char>>();
        Assert.True(lookup.TryGetValue("b".AsSpan(), out var actual, out _));
        Assert.Same(h.Slots.Last.Key, actual); // one string object, shared by the index and the slot
        Assert.Equal(["b"], lookup.Remove("a".AsSpan()).Keys.ToArray());
    }

    [Fact]
    public void Set_SpanOperations()
    {
        var s = ImmSet<string>.Empty.Mutate(static sc =>
        {
            var alt = sc.GetAlternateLookup<ReadOnlySpan<char>>();
            Assert.True(alt.Add("EURUSD".AsSpan()));
            Assert.False(alt.Add("EURUSD".AsSpan()));
            Assert.True(alt.Add("BTCUSDT".AsSpan()));
        });
        var l = s.GetAlternateLookup<ReadOnlySpan<char>>();
        Assert.True(l.Contains("BTCUSDT".AsSpan()));
        Assert.Single(l.Remove("EURUSD".AsSpan()));
    }

    [Fact]
    public void Unsupported_AlternateType_IsReported()
    {
        Assert.False(ImmMap<int, int>.Empty.TryGetAlternateLookup<string>(out _));
        Assert.Throws<InvalidOperationException>(() => ImmOrderedMap<int, int>.Empty.GetAlternateLookup<string>());
    }
}

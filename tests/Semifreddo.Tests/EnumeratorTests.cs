using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// The span-based ref struct enumerators against independent traversals: <c>Chunks</c> (FLeafCursor) for flex vectors,
/// indexing for vectors. Deep trees exercise the flex enumerator's multi-level ascent (path re-walk from the root).
/// </summary>
public class EnumeratorTests
{
    private static int[] ViaEnumerator(ImmFlexVector<int> v)
    {
        var a = new int[v.Count];
        var i = 0;
        foreach (ref readonly var x in v) a[i++] = x;
        Assert.Equal(v.Count, i);
        return a;
    }

    private static int[] ViaChunks(ImmFlexVector<int> v)
    {
        var a = new List<int>(v.Count);
        foreach (var c in v.Chunks) foreach (var x in c) a.Add(x);
        return [.. a];
    }

    public static IEnumerable<object[]> FlexShapes()
    {
        foreach (var n in new[] { 0, 1, 31, 32, 33, 1024, 1025, 32 * 32 * 32 + 7, 1_100_000 })
            yield return [$"append {n}", (Func<ImmFlexVector<int>>)(() => ImmFlexVector.Create(Enumerable.Range(0, n).ToArray()))];
        yield return ["relaxed concat 300k", (Func<ImmFlexVector<int>>)(() =>
        {
            var rnd = new Random(3);
            var v = ImmFlexVector<int>.Empty;
            for (var i = 0; i < 300_000;) { var k = 1 + rnd.Next(5000); v += ImmFlexVector.Create(Enumerable.Range(i, k).ToArray()); i += k; }
            return v;
        })];
        yield return ["slices + middle edits 1.1M", (Func<ImmFlexVector<int>>)(() =>
        {
            var rnd = new Random(5);
            var v = ImmFlexVector.Create(Enumerable.Range(0, 1_100_000).ToArray());
            v = v[777..1_090_001] + v[5..40_000];
            for (var e = 0; e < 300; e++)
            {
                var at = rnd.Next(v.Count);
                v = rnd.Next(2) == 0 ? v.Insert(at, -e) : v.RemoveRange(at, Math.Min(v.Count - at, rnd.Next(1, 200)));
            }
            return v;
        })];
    }

    [Theory, MemberData(nameof(FlexShapes))]
    public void FlexEnumerator_MatchesChunks(string name, Func<ImmFlexVector<int>> make)
    {
        var v = make();
        Assert.True(ViaChunks(v).AsSpan().SequenceEqual(ViaEnumerator(v)), name);

        var e = v.GetEnumerator();                       // exhausted enumerators stay exhausted; Reset restarts
        for (var n = 0; e.MoveNext(); n++) Assert.True(n < v.Count, "runaway enumerator");
        Assert.False(e.MoveNext());
        e.Reset();
        Assert.Equal(v.Count > 0, e.MoveNext());
        if (v.Count > 0) Assert.Equal(v[0], e.Current);
    }

    [Fact]
    public void VectorEnumerator_MatchesIndexing()
    {
        foreach (var n in new[] { 0, 1, 31, 32, 33, 1024, 1056, 1057, 33_000, 1_100_000 })
        {
            var v = ImmVector.Create(Enumerable.Range(0, n).ToArray());
            var i = 0;
            foreach (ref readonly var x in v) Assert.Equal(i++, x);
            Assert.Equal(n, i);
        }
    }

    [Fact]
    public void HoledSequences_SkipHolesAcrossLeafBoundaries()
    {
        var order = ImmPkTopoOrder<int, MinValueSentinel<int>>.Empty;
        var omap = ImmOrderedMap<int, int>.Empty;
        for (var i = 0; i < 5000; i++) { order = order.Add(i); omap = omap.SetItem(i, i); }
        var keep = new List<int>();
        for (var i = 0; i < 5000; i++)
        {
            var drop = (i / 32) % 3 == 1 || i % 5 == 0;    // whole leaves of holes plus scattered ones
            if (drop) { order = order.Remove(i); omap = omap.Remove(i); } else keep.Add(i);
        }
        var seen = new List<int>();
        foreach (var x in order) { seen.Add(x); Assert.True(seen.Count <= order.Count, "runaway enumerator"); }
        Assert.Equal(keep, seen);
        seen.Clear();
        foreach (var kv in omap) { seen.Add(kv.Key); Assert.True(seen.Count <= omap.Count, "runaway enumerator"); }
        Assert.Equal(keep, seen);
    }

    // ---- CHAMP (ImmSet / ImmMap / ImmDag): the span-based ref struct walk against the heap-safe cursor (IEnumerable).

    private static T[] Duck<T>(ImmSet<T> s) where T : notnull
    {
        var a = new List<T>(s.Count);
        foreach (ref readonly var x in s) { a.Add(x); Assert.True(a.Count <= s.Count, "runaway enumerator"); }
        return [.. a];
    }

    private static KeyValuePair<K, V>[] Duck<K, V>(ImmMap<K, V> m) where K : notnull
    {
        var a = new List<KeyValuePair<K, V>>(m.Count);
        var e = m.GetEnumerator();
        while (e.MoveNext())
        {
            Assert.Equal(e.Current.Key, e.CurrentKey);
            Assert.Equal(e.Current.Value, e.CurrentValue);
            a.Add(e.Current);
            Assert.True(a.Count <= m.Count, "runaway enumerator");
        }
        return [.. a];
    }

    public static IEnumerable<object[]> ChampSizes() => new[] { 0, 1, 2, 31, 33, 1000, 60_000, 1_000_000 }.Select(n => new object[] { n });

    [Theory, MemberData(nameof(ChampSizes))]
    public void ChampEnumerator_MatchesCursor_IntKeys(int n)
    {
        var set = ImmSet.Create(Enumerable.Range(0, n).Select(i => i * 7919).ToArray());
        Assert.Equal(((IEnumerable<int>)set).ToArray(), Duck(set));
        if (n > 100_000) return;
        var map = ImmMap.CreateRange(Enumerable.Range(0, n).Select(i => KeyValuePair.Create(i, -i)));
        Assert.Equal(((IEnumerable<KeyValuePair<int, int>>)map).ToArray(), Duck(map));
    }

    [Fact]
    public void ChampEnumerator_HandlesCollisionsAndDataLessNodes()
    {
        // Hash & 3: every key lands in one of four collision nodes at the deepest level, under data-less branches.
        var bad = ImmSet.Create(Enumerable.Range(0, 200).Select(i => new BadKey(i)).ToArray());
        Assert.Equal(((IEnumerable<BadKey>)bad).ToArray(), Duck(bad));
        var badMap = ImmMap.CreateRange(Enumerable.Range(0, 200).Select(i => KeyValuePair.Create(new BadKey(i), i)));
        Assert.Equal(((IEnumerable<KeyValuePair<BadKey, int>>)badMap).ToArray(), Duck(badMap));

        // Removals leave branch-only nodes (children, no entries) on the path: the walk must pass straight through.
        var set = ImmSet.Create(Enumerable.Range(0, 20_000).ToArray());
        for (var i = 0; i < 20_000; i++) if (i % 3 != 0) set = set.Remove(i);
        Assert.Equal(((IEnumerable<int>)set).ToArray(), Duck(set));

        var strings = ImmSet.Create(Enumerable.Range(0, 5000).Select(i => $"key-{i}").ToArray());
        Assert.Equal(((IEnumerable<string>)strings).ToArray(), Duck(strings));

        var e = set.GetEnumerator();                     // exhausted stays exhausted; Reset restarts
        for (var k = 0; e.MoveNext(); k++) Assert.True(k < set.Count, "runaway enumerator");
        Assert.False(e.MoveNext());
        e.Reset();
        Assert.True(e.MoveNext());
    }

    [Fact]
    public void DagEnumerator_MatchesCursor()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(3000, static (s, n) =>
        {
            for (var i = 0; i < n; i++) s.AddNode(i, -i);
            for (var i = 1; i < n; i++) s.AddEdge(i / 2, i);
        }, new DagBatchOptions { SkipCycleCheck = true });
        var viaCursor = ((IEnumerable<DagNodeView<int, int, NoPayload>>)dag).Select(v => (v.Id, v.AlternateKey, v.Successors.Count, v.Predecessors.Count)).ToArray();
        var viaDuck = new List<(int, int, int, int)>();
        foreach (var v in dag) viaDuck.Add((v.Id, v.AlternateKey, v.Successors.Count, v.Predecessors.Count));
        Assert.Equal(viaCursor, viaDuck);
    }

    // ---- Edge sets: EdgeSet<N>.Enumerator and Adjacency<N>.Enumerator against the heap-safe cursor.

    private static int[] Duck(EdgeSet<int> e)
    {
        var a = new List<int>(e.Count);
        foreach (var x in e) { a.Add(x); Assert.True(a.Count <= e.Count, "runaway enumerator"); }
        return [.. a];
    }

    private static int[] ViaAdjacency(Adjacency<int> adj)
    {
        var a = new List<int>(adj.Count);
        foreach (var x in adj) { a.Add(x); Assert.True(a.Count <= adj.Count, "runaway enumerator"); }
        return [.. a];
    }

    private static int[] Successors<TGraph>(TGraph g, int id) where TGraph : IDagAdjacency<int> => ViaAdjacency(g.Successors(id));
    private static int[] Predecessors<TGraph>(TGraph g, int id) where TGraph : IDagAdjacency<int> => ViaAdjacency(g.Predecessors(id));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(15)] [InlineData(16)] [InlineData(17)] [InlineData(100)] [InlineData(5000)]
    public void EdgeEnumerators_MatchCursor(int fanOut)
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(fanOut, static (s, n) =>
        {
            for (var i = 0; i <= n; i++) s.AddNode(i, i);
            for (var k = 1; k <= n; k++) { s.AddEdge(0, k); if (k > 1) s.AddEdge(k, 1); }
        }, new DagBatchOptions { SkipCycleCheck = true });
        foreach (var v in dag)
        {
            int[] succ = ((IEnumerable<int>)v.Successors).ToArray(), pred = ((IEnumerable<int>)v.Predecessors).ToArray();
            Assert.Equal(succ, Duck(v.Successors));
            Assert.Equal(pred, Duck(v.Predecessors));
            Assert.Equal(succ, Successors(dag, v.Id));
            Assert.Equal(pred, Predecessors(dag, v.Id));
        }

        // Shrink a large set back across the inline threshold: whatever storage results, enumeration must agree.
        var shrunk = dag;
        for (var k = 1; k <= fanOut - 3; k++) shrunk = shrunk.RemoveEdge(0, k);
        var s0 = shrunk.Single(v => v.Id == 0).Successors;
        Assert.Equal(((IEnumerable<int>)s0).ToArray(), Duck(s0));
        Assert.Equal(((IEnumerable<int>)s0).ToArray(), Successors(shrunk, 0));

        var e = s0.GetEnumerator();                      // exhausted stays exhausted; Reset restarts
        for (var k = 0; e.MoveNext(); k++) Assert.True(k < s0.Count, "runaway enumerator");
        Assert.False(e.MoveNext());
        e.Reset();
        Assert.Equal(s0.Count > 0, e.MoveNext());
    }

    [Fact]
    public void Adjacency_CallerSpanAndCollectionModes()
    {
        int[] items = [5, 3, 9, 1];
        Assert.Equal(items, ViaAdjacency(new Adjacency<int>(items.AsSpan())));
        Assert.Equal(items, ViaAdjacency(new Adjacency<int>((IReadOnlyCollection<int>)new List<int>(items))));
        Assert.Empty(ViaAdjacency(new Adjacency<int>(ReadOnlySpan<int>.Empty)));
        Assert.Empty(ViaAdjacency(default));
    }
}

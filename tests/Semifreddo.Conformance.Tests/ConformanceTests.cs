using Xunit;
using ZLinq;

namespace Semifreddo.Conformance;

/// <summary>
/// Every Semifreddo collection against the <see cref="IRefEnumerable{T, TEnumerator}"/> contract and, through the
/// host-side <see cref="FromRefEnumerable{TSource, TEnumerator, T}"/> bridge, against ZLinq. Shapes cover the storage
/// boundaries (empty, single chunk, chunk edges, relaxed RRB leaves, CHAMP depth, inline vs CHAMP edge sets,
/// tombstones and removal holes) and both value and reference element types.
/// </summary>
public class ConformanceTests
{
    private static Case Make<TSource, TEnumerator, T>(string name, TSource s)
        where TSource : struct, IRefEnumerable<T, TEnumerator>
        where TEnumerator : struct, IEnumerator<T>, allows ref struct
        => new Case<TSource, TEnumerator, T>(name, s);

    private static ImmFlexVector<int> Relaxed(int n, int seed)
    {
        var rnd = new Random(seed);
        var v = ImmFlexVector<int>.Empty;
        for (var i = 0; i < n;)
        {
            var k = Math.Min(n - i, 1 + rnd.Next(70));
            v += ImmFlexVector.Create(Enumerable.Range(i, k).ToArray());
            i += k;
        }
        return v;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (var n in new[] { 0, 1, 32, 33, 1057, 5000 })
            yield return [Make<ImmVector<int>, ImmVector<int>.Enumerator, int>("ImmVector<int>", ImmVector.Create(Enumerable.Range(0, n).Select(i => i * 3 + 1).ToArray()))];
        yield return [Make<ImmVector<string>, ImmVector<string>.Enumerator, string>("ImmVector<string>", ImmVector.Create(Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray()))];

        foreach (var n in new[] { 0, 70, 1057, 5000 })
            yield return [Make<ImmFlexVector<int>, ImmFlexVector<int>.Enumerator, int>("ImmFlexVector<int> relaxed", Relaxed(n, n))];
        yield return [Make<ImmFlexVector<int>, ImmFlexVector<int>.Enumerator, int>("ImmFlexVector<int> sliced", Relaxed(3000, 1)[333..2777])];

        foreach (var n in new[] { 0, 700 })
            yield return [Make<ImmSet<int>, ImmSet<int>.Enumerator, int>("ImmSet<int>", ImmSet.Create(Enumerable.Range(0, n).Select(i => i * 7919 % 10007).ToArray()))];
        yield return [Make<ImmSet<string>, ImmSet<string>.Enumerator, string>("ImmSet<string>", ImmSet.Create(Enumerable.Range(0, 200).Select(i => $"k{i}").ToArray()))];

        var pairs = Enumerable.Range(0, 700).Select(i => KeyValuePair.Create(i * 31 % 1009, i)).ToArray();
        yield return [Make<ImmMap<int, int>, ImmMap<int, int>.Enumerator, KeyValuePair<int, int>>("ImmMap<int, int>", ImmMap.CreateRange(pairs))];
        var omap = ImmOrderedMap.CreateRange(pairs.Select(p => KeyValuePair.Create($"k{p.Key}", p.Value)));
        for (var i = 0; i < 700; i += 3) omap = omap.Remove($"k{pairs[i].Key}");    // tombstones
        yield return [Make<ImmOrderedMap<string, int>, ImmOrderedMap<string, int>.Enumerator, KeyValuePair<string, int>>("ImmOrderedMap<string, int> with tombstones", omap)];

        var dag = ImmDag<int, int, NoPayload>.Empty.Mutate(60, static (s, n) =>
        {
            for (var i = 0; i < n; i++) s.AddNode(i, i);
            for (var i = 1; i < 4; i++) s.AddEdge(0, i);    // inline edge set
            for (var i = 2; i < n; i++) s.AddEdge(1, i);    // CHAMP edge set
        }, new DagBatchOptions { SkipCycleCheck = true });
        yield return [Make<ImmDag<int, int, NoPayload>, ImmDag<int, int, NoPayload>.Enumerator, DagNodeView<int, int, NoPayload>>("ImmDag nodes", dag)];
        foreach (var (label, id) in new[] { ("inline", 0), ("CHAMP", 1), ("empty", 59) })
            yield return [Make<EdgeSet<int>, EdgeSet<int>.Enumerator, int>($"EdgeSet {label}", dag.Single(v => v.Id == id).Successors)];

        var order = ImmPkTopoOrder<int, MinValueSentinel<int>>.Empty;
        var leveled = ImmLeveledPkTopoOrder<int, MinValueSentinel<int>>.Empty;
        for (var i = 0; i < 1000; i++) { order = order.Add(i); leveled = leveled.Add(i); }
        for (var i = 0; i < 1000; i += 7) { order = order.Remove(i); leveled = leveled.Remove(i); }   // removal holes
        yield return [Make<ImmPkTopoOrder<int, MinValueSentinel<int>>, TopoEnumerator<int, MinValueSentinel<int>>, int>("ImmPkTopoOrder with holes", order)];
        yield return [Make<ImmLeveledPkTopoOrder<int, MinValueSentinel<int>>, TopoEnumerator<int, MinValueSentinel<int>>, int>("ImmLeveledPkTopoOrder with holes", leveled)];
    }

    [Theory, MemberData(nameof(Cases))]
    public void Contract(Case c) => c.CheckContract();

    [Theory, MemberData(nameof(Cases))]
    public void ZLinq_AgreesWithLinq(Case c) => c.CheckZLinq();

    [Theory, MemberData(nameof(Cases))]
    public void AllocationFree(Case c) => c.CheckAllocationFree();

    [Fact]
    public void BridgeOverloads_BeatZLinqsBoxingFallback()
    {
        // Explicit types: compile-time proof that the bridge's overloads bind, not ZLinq's AsValueEnumerable(IEnumerable<T>).
        var v = ImmVector.Create([1, 2]).AsValueEnumerable();
        var m = ImmMap<int, int>.Empty.SetItem(1, 2).AsValueEnumerable();
        Assert.Equal(3, v.Sum());
        Assert.Equal(2, m.Sum(static kv => kv.Value));
    }
}

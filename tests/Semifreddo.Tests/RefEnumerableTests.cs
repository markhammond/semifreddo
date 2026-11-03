using Xunit;

namespace Semifreddo.Tests;

/// <summary>The dependency-free <see cref="IRefEnumerable{T, TEnumerator}"/> contract, exercised as a host would.</summary>
public class RefEnumerableTests
{
    // What any host adapter does: everything through constrained calls on structs, nothing boxed.
    private static long Sum<TSource, TEnumerator>(TSource source, Func<int, long>? map = null)
        where TSource : struct, IRefEnumerable<int, TEnumerator>
        where TEnumerator : struct, IEnumerator<int>, allows ref struct
    {
        long s = 0;
        var e = source.GetEnumerator();
        while (e.MoveNext()) s += map is null ? e.Current : map(e.Current);
        e.Dispose();
        return s;
    }

    private static int[] Materialize<TSource, TEnumerator>(TSource source)
        where TSource : struct, IRefEnumerable<int, TEnumerator>
        where TEnumerator : struct, IEnumerator<int>, allows ref struct
    {
        var a = new int[source.Count];
        if (source.TryGetSpan(out var span)) { span.CopyTo(a); return a; }
        if (source.TryCopyTo(0, a)) return a;
        var i = 0;
        var e = source.GetEnumerator();
        while (e.MoveNext()) a[i++] = e.Current;
        return a;
    }

    [Fact]
    public void PublicEnumerators_AreRefStructs()
    {
        var enumerators = typeof(ImmVector<>).Assembly.GetExportedTypes()
            .Where(t => t.Name == "Enumerator" || t.Name.StartsWith("TopoEnumerator", StringComparison.Ordinal))
            .ToArray();
        Assert.True(enumerators.Length >= 9, string.Join(", ", enumerators.Select(t => t.FullName)));
        Assert.All(enumerators, t => Assert.True(t.IsByRefLike, $"{t.FullName} is not a ref struct"));
    }

    [Fact]
    public void GenericHost_SeesTheSameElementsAsLinq()
    {
        var a = Enumerable.Range(0, 3000).Select(i => i * 7 % 3001).ToArray();
        var vec = ImmVector.Create<int>(a);
        var flex = ImmFlexVector.Create<int>(a[..1000]) + ImmFlexVector.Create<int>(a[1000..]);
        var set = ImmSet.Create<int>(a);
        var order = IntOrderOnly.Empty;
        foreach (var x in a) order = order.Add(x);
        var leveled = IntTopoOrder.Empty.Add(5).Add(9).Remove(5).Add(1);   // a removal hole
        var small = ImmDag<int, int, NoPayload>.Empty.Mutate(0, static (s, _) =>
        {
            for (var i = 0; i < 40; i++) s.AddNode(i, i);
            for (var i = 1; i < 4; i++) s.AddEdge(0, i);    // 3 successors: inline array (span fast path)
            for (var i = 2; i < 40; i++) s.AddEdge(1, i);   // 38 successors: CHAMP set (enumerated)
        }, new DagBatchOptions { SkipCycleCheck = true });

        Assert.Equal(a.Sum(x => (long)x), Sum<ImmVector<int>, ImmVector<int>.Enumerator>(vec));
        Assert.Equal(a.Sum(x => (long)x), Sum<ImmFlexVector<int>, ImmFlexVector<int>.Enumerator>(flex));
        Assert.Equal(set.Sum(x => (long)x), Sum<ImmSet<int>, ImmSet<int>.Enumerator>(set));
        Assert.Equal(order.Sum(x => (long)x), Sum<IntOrderOnly, TopoEnumerator<int, MinValueSentinel<int>>>(order));
        Assert.Equal(a, Materialize<ImmVector<int>, ImmVector<int>.Enumerator>(vec));
        Assert.Equal(a, Materialize<ImmFlexVector<int>, ImmFlexVector<int>.Enumerator>(flex));
        Assert.Equal(set.ToArray(), Materialize<ImmSet<int>, ImmSet<int>.Enumerator>(set));
        Assert.Equal([9, 1], Materialize<IntTopoOrder, TopoEnumerator<int, MinValueSentinel<int>>>(leveled));
        foreach (var n in small)
            Assert.Equal(n.Successors.ToArray(), Materialize<EdgeSet<int>, EdgeSet<int>.Enumerator>(n.Successors));
    }

    [Fact]
    public void Vectors_FastPaths_FollowTheContract()
    {
        foreach (var n in new[] { 0, 1, 31, 32, 33, 100, 1057 })
        {
            var a = Enumerable.Range(0, n).ToArray();
            IRefEnumerable<int, ImmVector<int>.Enumerator> vec = ImmVector.Create<int>(a);   // boxed here only for brevity
            var rnd = new Random(n);
            var parts = ImmFlexVector<int>.Empty;
            for (var i = 0; i < n;) { var k = Math.Min(n - i, 1 + rnd.Next(50)); parts += ImmFlexVector.Create<int>(a.AsSpan(i, k)); i += k; }
            IRefEnumerable<int, ImmFlexVector<int>.Enumerator> flex = parts;

            Assert.Equal(n <= 32, vec.TryGetSpan(out var vs));       // one chunk only
            if (n <= 32) Assert.Equal(a, vs.ToArray());
            if (flex.TryGetSpan(out var fs)) Assert.Equal(a, fs.ToArray());

            for (var index = 0; index <= n; index++)
            foreach (var len in new[] { 0, 1, 5, 16, 17, 40, n + 3 })
            {
                var expected = Math.Min(len, n - index);
                var d1 = new int[len]; var d2 = new int[len];
                Assert.True(vec.TryCopyTo(index, d1));
                Assert.True(flex.TryCopyTo(index, d2));
                Assert.Equal(a.AsSpan(index, expected).ToArray(), d1[..expected]);
                Assert.Equal(a.AsSpan(index, expected).ToArray(), d2[..expected]);
                Assert.All(d1[expected..], x => Assert.Equal(0, x));   // nothing written past the copied range
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => vec.TryCopyTo(n + 1, new int[1]));
            Assert.Throws<ArgumentOutOfRangeException>(() => flex.TryCopyTo(-1, new int[1]));
        }
    }

    [Fact]
    public void HashedCollections_DeclineFastPaths()
    {
        IRefEnumerable<int, ImmSet<int>.Enumerator> set = ImmSet.Create<int>([1, 2, 3]);
        IRefEnumerable<int, TopoEnumerator<int, MinValueSentinel<int>>> order = IntOrderOnly.Empty.Add(1).Add(2);
        Assert.False(set.TryGetSpan(out _));
        Assert.False(set.TryCopyTo(0, new int[3]));
        Assert.False(order.TryGetSpan(out _));
        Assert.False(order.TryCopyTo(0, new int[2]));
        Assert.True(((IRefEnumerable<int, ImmSet<int>.Enumerator>)ImmSet<int>.Empty).TryGetSpan(out var empty) && empty.IsEmpty);
    }

    [Fact]
    public void GenericHost_IsAllocationFree()
    {
        var vec = ImmVector.Create(Enumerable.Range(0, 1000).ToArray());
        var map = ImmMap.CreateRange(Enumerable.Range(0, 1000).Select(i => KeyValuePair.Create(i, i)));
        var order = IntTopoOrder.Empty;
        for (var i = 0; i < 1000; i++) order = order.Add(i);
        var buffer = new int[1000];
        long sink = 0;
        Assert.Equal(0, Alloc.SteadyState(() => sink += Sum<ImmVector<int>, ImmVector<int>.Enumerator>(vec)));
        Assert.Equal(0, Alloc.SteadyState(() => sink += Sum<IntTopoOrder, TopoEnumerator<int, MinValueSentinel<int>>>(order)));
        Assert.Equal(0, Alloc.SteadyState(() => sink += Copy<ImmVector<int>, ImmVector<int>.Enumerator>(vec, buffer)));
        Assert.Equal(0, Alloc.SteadyState(() => { foreach (var kv in map) sink += kv.Value; }));
        Assert.NotEqual(0, sink);

        static int Copy<TSource, TEnumerator>(TSource s, int[] into)
            where TSource : struct, IRefEnumerable<int, TEnumerator>
            where TEnumerator : struct, IEnumerator<int>, allows ref struct
            => s.TryCopyTo(0, into) ? into[^1] : -1;
    }
}

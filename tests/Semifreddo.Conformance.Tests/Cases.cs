using Xunit;
using ZLinq;
using ZLinq.Internal;

namespace Semifreddo.Conformance;

/// <summary>One collection under test. The generic subclass carries the static types a host adapter would see.</summary>
public abstract class Case
{
    public abstract string Name { get; }

    /// <summary>The <see cref="IRefEnumerable{T, TEnumerator}"/> contract, checked through constrained calls only.</summary>
    public abstract void CheckContract();

    /// <summary>ZLinq over the bridge agrees with LINQ over the <c>IEnumerable</c> path, fast paths included.</summary>
    public abstract void CheckZLinq();

    /// <summary>Duck-typed enumeration and a ZLinq query allocate nothing once warm.</summary>
    public abstract void CheckAllocationFree();

    public override string ToString() => Name;
}

public sealed class Case<TSource, TEnumerator, T>(string name, TSource source) : Case
    where TSource : struct, IRefEnumerable<T, TEnumerator>
    where TEnumerator : struct, IEnumerator<T>, allows ref struct
{
    private static readonly EqualityComparer<T> Eq = EqualityComparer<T>.Default;

    public override string Name => $"{name} (n={source.Count})";

    // The reference sequence: the heap-safe IEnumerable path, as plain LINQ sees it.
    private T[] Expected() => ((IEnumerable<T>)source).ToArray();

    private ValueEnumerable<FromRefEnumerable<TSource, TEnumerator, T>, T> Q() => new(new(source));

    private static T[] DuckTyped(TSource s)
    {
        var list = new List<T>(s.Count);
        var e = s.GetEnumerator();
        try
        {
            while (e.MoveNext())
            {
                list.Add(e.Current);
                if (list.Count > s.Count) throw new Xunit.Sdk.XunitException($"enumerator yields more than Count ({s.Count}) elements");
            }
        }
        finally { e.Dispose(); }
        return [.. list];
    }

    private static IEnumerable<int> Probes(int n) =>
        n <= 70 ? Enumerable.Range(0, n + 1)
                : new[] { 0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, n / 3, n / 2, n - 33, n - 32, n - 17, n - 1, n }.Where(i => i >= 0 && i <= n).Distinct();

    public override void CheckContract()
    {
        var expected = Expected();
        var n = expected.Length;
        Assert.Equal(n, source.Count);
        Assert.Equal(expected, DuckTyped(source));                     // same elements, same order as IEnumerable

        if (source.TryGetSpan(out var span)) Assert.Equal(expected, span.ToArray());

        foreach (var index in Probes(n))
        foreach (var len in new[] { 0, 1, 5, 16, 17, 40, n + 3 })
        {
            var d = new T[len];
            if (!source.TryCopyTo(index, d)) { Assert.All(d, x => Assert.True(Eq.Equals(x, default))); continue; }
            var copied = Math.Min(len, n - index);
            Assert.Equal(expected.AsSpan(index, copied).ToArray(), d[..copied]);
            Assert.All(d[copied..], x => Assert.True(Eq.Equals(x, default)));   // nothing written past the range
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => source.TryCopyTo(-1, new T[1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.TryCopyTo(n + 1, new T[1]));
    }

    public override void CheckZLinq()
    {
        var expected = Expected();
        var n = expected.Length;
        Assert.Equal(expected, Q().ToArray());
        Assert.Equal(expected, Q().Where(static _ => true).ToArray());   // hides count/span: pure TryGetNext path
        Assert.Equal(expected, Q().ToList());
        Assert.Equal(n, Q().Count());
        Assert.Equal(n > 0, Q().Any());
        Assert.Equal(expected.Reverse().ToArray(), Q().Reverse().ToArray());
        Assert.Equal(expected.Skip(n / 3).Take(50).ToArray(), Q().Skip(n / 3).Take(50).ToArray());
        Assert.Equal(expected.Skip(n / 2).ToArray(), Q().Skip(n / 2).ToArray());
        if (n > 0)
        {
            Assert.Equal(expected[0], Q().First());
            Assert.Equal(expected[^1], Q().Last());
            Assert.Equal(expected[n / 2], Q().ElementAt(n / 2));
            Assert.Equal(expected[^1], Q().ElementAt(^1));
            Assert.Equal(expected[0], Q().ElementAt(^n));
        }
        Assert.Equal(expected.ElementAtOrDefault(n), Q().ElementAtOrDefault(n));

        // The bridge's Index mapping against ZLinq's own reference slicing (it may decline, never disagree).
        foreach (var o in Probes(n))
        foreach (var fromEnd in new[] { false, true })
        foreach (var len in new[] { 0, 1, 3, 17, n + 2 })
        {
            var offset = new Index(o, fromEnd);
            var d = new T[len];
            var ok = Q().Enumerator.TryCopyTo(d, offset);
            var okRef = EnumeratorHelper.TryGetSlice<T>(expected, offset, len, out var slice);
            if (!okRef) Assert.False(ok, $"bridge copied at {offset} (len {len}) where ZLinq's contract says no");
            else if (ok) Assert.Equal(slice.ToArray(), d[..slice.Length]);
        }
    }

    public override void CheckAllocationFree()
    {
        long sink = 0;
        Assert.Equal(0, Alloc.SteadyState(() => sink += DuckTypedCount(source)));
        Assert.Equal(0, Alloc.SteadyState(() => sink += Q().Where(static _ => true).Count()));
        Assert.Equal(source.Count * 2L * 10, sink);   // 5 warm-ups + 5 trials, two probes

        static int DuckTypedCount(TSource s)
        {
            var c = 0;
            var e = s.GetEnumerator();
            while (e.MoveNext())
                if (++c > s.Count) throw new InvalidOperationException("enumerator yields more than Count elements");
            return c;
        }
    }
}

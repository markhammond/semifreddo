using System.Runtime.CompilerServices;
using ZLinq;

namespace Semifreddo.Conformance;

/// <summary>
/// Host-side ZLinq bridge (a test dependency only: Semifreddo itself references no LINQ library). The whole bridge is one
/// generic source over Semifreddo's <see cref="IRefEnumerable{T, TEnumerator}"/>. Every call on <typeparamref name="TSource"/> and <typeparamref name="TEnumerator"/> is a constrained call on a struct, so
/// neither the collection nor its ref struct enumerator is ever boxed. Another library (StructLinq, HyperLinq, an
/// in-house one) needs the same ~40 lines against its own interface.
/// </summary>
public ref struct FromRefEnumerable<TSource, TEnumerator, T> : IValueEnumerator<T>
    where TSource : struct, IRefEnumerable<T, TEnumerator>
    where TEnumerator : struct, IEnumerator<T>, allows ref struct
{
    private readonly TSource _source;
    private TEnumerator _e;
    private bool _started;

    public FromRefEnumerable(TSource source)
    {
        _source = source;
        _e = default;
        _started = false;
    }

    public readonly bool TryGetNonEnumeratedCount(out int count)
    {
        count = _source.Count;
        return true;
    }

    public readonly bool TryGetSpan(out ReadOnlySpan<T> span) => _source.TryGetSpan(out span);

    // ZLinq's convention: the offset (possibly from the end) must land inside the sequence; the copy is clamped.
    public readonly bool TryCopyTo(scoped Span<T> destination, Index offset)
    {
        int count = _source.Count, start = offset.GetOffset(count);
        if ((uint)start >= (uint)count) return false;
        return _source.TryCopyTo(start, destination[..Math.Min(destination.Length, count - start)]);
    }

    // Hot path kept tiny so it inlines into ZLinq's operator loops; first-call setup lives out of line.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetNext(out T current)
    {
        if (_started && _e.MoveNext())
        {
            current = _e.Current;
            return true;
        }
        return TryGetFirst(out current);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryGetFirst(out T current)
    {
        // Created on first use, as ZLinq recommends: enumerators are copied by value along a query chain.
        if (!_started)
        {
            _e = _source.GetEnumerator();
            _started = true;
            if (_e.MoveNext())
            {
                current = _e.Current;
                return true;
            }
        }
        current = default!;
        return false;
    }

    public void Dispose()
    {
        if (_started) _e.Dispose();
    }
}

/// <summary>
/// One-line overloads so the compiler can infer the adapter's type arguments. They are more specific than ZLinq's
/// boxing <c>AsValueEnumerable(this IEnumerable&lt;T&gt;)</c>, so they always win.
/// </summary>
public static class SemifreddoZLinqExtensions
{
    public static ValueEnumerable<FromRefEnumerable<ImmVector<T>, ImmVector<T>.Enumerator, T>, T> AsValueEnumerable<T>(this ImmVector<T> source) => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmFlexVector<T>, ImmFlexVector<T>.Enumerator, T>, T> AsValueEnumerable<T>(this ImmFlexVector<T> source) => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmSet<T>, ImmSet<T>.Enumerator, T>, T> AsValueEnumerable<T>(this ImmSet<T> source)
        where T : notnull => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmMap<K, V>, ImmMap<K, V>.Enumerator, KeyValuePair<K, V>>, KeyValuePair<K, V>> AsValueEnumerable<K, V>(this ImmMap<K, V> source)
        where K : notnull => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmOrderedMap<K, V>, ImmOrderedMap<K, V>.Enumerator, KeyValuePair<K, V>>, KeyValuePair<K, V>> AsValueEnumerable<K, V>(this ImmOrderedMap<K, V> source)
        where K : notnull => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmDag<N, A, V>, ImmDag<N, A, V>.Enumerator, DagNodeView<N, A, V>>, DagNodeView<N, A, V>> AsValueEnumerable<N, A, V>(this ImmDag<N, A, V> source)
        where N : notnull where A : notnull => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<EdgeSet<N>, EdgeSet<N>.Enumerator, N>, N> AsValueEnumerable<N>(this EdgeSet<N> source)
        where N : notnull => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmPkTopoOrder<N, TSentinel>, TopoEnumerator<N, TSentinel>, N>, N> AsValueEnumerable<N, TSentinel>(this ImmPkTopoOrder<N, TSentinel> source)
        where N : notnull where TSentinel : struct, INodeSentinel<N> => new(new(source));

    public static ValueEnumerable<FromRefEnumerable<ImmLeveledPkTopoOrder<N, TSentinel>, TopoEnumerator<N, TSentinel>, N>, N> AsValueEnumerable<N, TSentinel>(this ImmLeveledPkTopoOrder<N, TSentinel> source)
        where N : notnull where TSentinel : struct, INodeSentinel<N> => new(new(source));
}

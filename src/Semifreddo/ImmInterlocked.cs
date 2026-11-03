using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

/// <summary>
/// Lock-free atomic update of a shared persistent value (the immer::atom role, in the style of
/// System.Collections.Immutable.ImmutableInterlocked). Works because every Imm* value is a single reference.
/// The <c>transformer</c> may run more than once under contention, so it must be pure.
/// </summary>
public static class ImmInterlocked
{
    public static ImmVector<T> Read<T>(ref readonly ImmVector<T> location)
        => new(Volatile.Read(ref Unsafe.As<ImmVector<T>, VHead<T>?>(ref Unsafe.AsRef(in location))));

    public static ImmMap<K, V> Read<K, V>(ref readonly ImmMap<K, V> location) where K : notnull
        => new(Volatile.Read(ref Unsafe.As<ImmMap<K, V>, ChampNode<MapEntry<K, V>>?>(ref Unsafe.AsRef(in location))));

    public static ImmSet<T> Read<T>(ref readonly ImmSet<T> location) where T : notnull
        => new(Volatile.Read(ref Unsafe.As<ImmSet<T>, ChampNode<SetEntry<T>>?>(ref Unsafe.AsRef(in location))));

    public static bool Update<T, TArg>(ref ImmVector<T> location, Func<ImmVector<T>, TArg, ImmVector<T>> transformer, TArg arg)
    {
        ref var loc = ref Unsafe.As<ImmVector<T>, VHead<T>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<T>(ref ImmVector<T> location, Func<ImmVector<T>, ImmVector<T>> transformer)
        => Update(ref location, static (v, f) => f(v), transformer);

    public static bool Update<K, V, TArg>(ref ImmMap<K, V> location, Func<ImmMap<K, V>, TArg, ImmMap<K, V>> transformer, TArg arg)
        where K : notnull
    {
        ref var loc = ref Unsafe.As<ImmMap<K, V>, ChampNode<MapEntry<K, V>>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Root;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<K, V>(ref ImmMap<K, V> location, Func<ImmMap<K, V>, ImmMap<K, V>> transformer) where K : notnull
        => Update(ref location, static (v, f) => f(v), transformer);

    public static bool Update<T, TArg>(ref ImmSet<T> location, Func<ImmSet<T>, TArg, ImmSet<T>> transformer, TArg arg)
        where T : notnull
    {
        ref var loc = ref Unsafe.As<ImmSet<T>, ChampNode<SetEntry<T>>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Root;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<T>(ref ImmSet<T> location, Func<ImmSet<T>, ImmSet<T>> transformer) where T : notnull
        => Update(ref location, static (v, f) => f(v), transformer);

    public static bool Update<T, TArg>(ref ImmFlexVector<T> location, Func<ImmFlexVector<T>, TArg, ImmFlexVector<T>> transformer, TArg arg)
    {
        ref var loc = ref Unsafe.As<ImmFlexVector<T>, FHead<T>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<T>(ref ImmFlexVector<T> location, Func<ImmFlexVector<T>, ImmFlexVector<T>> transformer)
        => Update(ref location, static (v, f) => f(v), transformer);

    public static bool Update<K, V, TArg>(ref ImmOrderedMap<K, V> location, Func<ImmOrderedMap<K, V>, TArg, ImmOrderedMap<K, V>> transformer, TArg arg)
        where K : notnull
    {
        ref var loc = ref Unsafe.As<ImmOrderedMap<K, V>, OHead<K, V>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<K, V>(ref ImmOrderedMap<K, V> location, Func<ImmOrderedMap<K, V>, ImmOrderedMap<K, V>> transformer)
        where K : notnull
        => Update(ref location, static (v, f) => f(v), transformer);

    public static bool Update<N, A, V, TArg>(ref ImmDag<N, A, V> location, Func<ImmDag<N, A, V>, TArg, ImmDag<N, A, V>> transformer, TArg arg)
        where N : notnull where A : notnull
    {
        ref var loc = ref Unsafe.As<ImmDag<N, A, V>, DagHead<N, A, V>?>(ref location);
        var cur = Volatile.Read(ref loc);
        while (true)
        {
            var next = transformer(new(cur), arg).Head;
            if (ReferenceEquals(next, cur)) return false;
            var seen = Interlocked.CompareExchange(ref loc, next, cur);
            if (ReferenceEquals(seen, cur)) return true;
            cur = seen;
        }
    }

    public static bool Update<N, A, V>(ref ImmDag<N, A, V> location, Func<ImmDag<N, A, V>, ImmDag<N, A, V>> transformer)
        where N : notnull where A : notnull
        => Update(ref location, static (v, f) => f(v), transformer);
}

using System.Collections;
using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

public static class ImmSet
{
    public static ImmSet<T> Create<T>(ReadOnlySpan<T> items) where T : notnull => ImmSet<T>.Empty.Union(items);

    public static ImmSet<T> CreateRange<T>(IEnumerable<T> items) where T : notnull => ImmSet<T>.Empty.Union(items);

    public static ImmSet<T>.Transient CreateTransient<T>() where T : notnull => new();
}

/// <summary>Persistent hash set (immer::set), CHAMP-backed. Same threading semantics as <see cref="ImmMap{K,V}"/>, and the same
/// equality: by version, not contents.</summary>
[CollectionBuilder(typeof(ImmSet), nameof(ImmSet.Create))]
public readonly struct ImmSet<T> : IReadOnlyCollection<T>, IRefEnumerable<T, ImmSet<T>.Enumerator>, IImmutable where T : notnull
{
    private readonly ChampNode<SetEntry<T>>? _root;

    internal ImmSet(ChampNode<SetEntry<T>>? root) => _root = root;

    internal ChampNode<SetEntry<T>>? Root => _root;

    public static ImmSet<T> Empty => default;

    public int Count => _root is null ? 0 : _root.Count;

    public bool IsEmpty => _root is null;

    public bool IsSameAs(ImmSet<T> other) => ReferenceEquals(_root, other._root);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(T item) => !Unsafe.IsNullRef(ref Champ<SetEntry<T>, T, DefaultHasher<T>>.Find(_root, item));

    public ImmSet<T> Add(T item)
    {
        var e = new SetEntry<T>(item);
        var hash = Champ<SetEntry<T>, T, DefaultHasher<T>>.HashOf(item);
        var r = _root;
        if (r is null)
        {
            var s = Champ<SetEntry<T>, T, DefaultHasher<T>>.Singleton(in e, hash, null);
            s.Count = 1;
            return new(s);
        }
        if (Contains(item)) return this; // a set "replace" is a no-op: keep identity, skip path copy
        var added = false;
        var nr = Champ<SetEntry<T>, T, DefaultHasher<T>>.Set(r, in e, item, hash, 0, null, ref added);
        nr.Count = r.Count + 1;
        return new(nr);
    }

    public ImmSet<T> Remove(T item)
    {
        var r = _root;
        if (r is null) return this;
        var removed = false;
        var nr = Champ<SetEntry<T>, T, DefaultHasher<T>>.Remove(
            r, item, Champ<SetEntry<T>, T, DefaultHasher<T>>.HashOf(item), 0, null, ref removed);
        if (!removed) return this;
        if (r.Count == 1) return default;
        nr.Count = r.Count - 1;
        return new(nr);
    }

    public ImmSet<T> Union(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return this;
        var t = Transient.Rent(_root);
        try
        {
            foreach (ref readonly var x in items) t.Add(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmSet<T> Union(IEnumerable<T> items)
    {
        var t = Transient.Rent(_root);
        try
        {
            foreach (var x in items) t.Add(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmSet<T> Union(ImmSet<T> other)
    {
        if (other.Count > Count) return other.Union(this);
        if (other.IsEmpty || IsSameAs(other)) return this;
        var t = Transient.Rent(_root);
        try
        {
            foreach (var x in other) t.Add(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmSet<T> Except(ImmSet<T> other)
    {
        if (other.IsEmpty || IsEmpty) return this;
        var t = Transient.Rent(_root);
        try
        {
            foreach (var x in other) t.Remove(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmSet<T> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(_root);
        try { batch(new Scope(t), arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmSet<T> Mutate(Action<Scope> batch)
    {
        var t = Transient.Rent(_root);
        try { batch(new Scope(t)); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public Transient ToTransient() => new(_root);

    private static IAlternateEqualityComparer<TAlt, T>? AlternateComparer<TAlt>() where TAlt : notnull, allows ref struct
        => EqualityComparer<T>.Default as IAlternateEqualityComparer<TAlt, T>;

    private static InvalidOperationException NoAlternate<TAlt>() where TAlt : allows ref struct
        => new($"EqualityComparer<{typeof(T).Name}>.Default does not implement IAlternateEqualityComparer<{typeof(TAlt).Name}, {typeof(T).Name}>.");

    /// <summary>Lookups by an alternate key type (cf. <c>HashSet.GetAlternateLookup</c>).</summary>
    public AlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
        => TryGetAlternateLookup<TAlt>(out var l) ? l : throw NoAlternate<TAlt>();

    public bool TryGetAlternateLookup<TAlt>(out AlternateLookup<TAlt> lookup) where TAlt : notnull, allows ref struct
    {
        var c = AlternateComparer<TAlt>();
        lookup = c is null ? default : new(_root, c);
        return c is not null;
    }

    public readonly struct AlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly ChampNode<SetEntry<T>>? _root;
        private readonly IAlternateEqualityComparer<TAlt, T> _cmp;

        internal AlternateLookup(ChampNode<SetEntry<T>>? root, IAlternateEqualityComparer<TAlt, T> cmp) { _root = root; _cmp = cmp; }

        public ImmSet<T> Set => new(_root);

        public bool Contains(TAlt item)
            => _root is not null && !Unsafe.IsNullRef(ref Champ<SetEntry<T>, T, DefaultHasher<T>>.FindAt(_root, new AltProbe<TAlt, T>(item, _cmp), 0));

        public bool TryGetValue(TAlt item, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T actualValue)
        {
            if (_root is not null)
            {
                ref var e = ref Champ<SetEntry<T>, T, DefaultHasher<T>>.FindAt(_root, new AltProbe<TAlt, T>(item, _cmp), 0);
                if (!Unsafe.IsNullRef(ref e)) { actualValue = e.Key; return true; }
            }
            actualValue = default;
            return false;
        }

        public ImmSet<T> Remove(TAlt item)
        {
            var r = _root;
            if (r is null) return default;
            var removed = false;
            var nr = Champ<SetEntry<T>, T, DefaultHasher<T>>.Remove(r, new AltProbe<TAlt, T>(item, _cmp), 0, null, ref removed);
            if (!removed) return new(r);
            if (r.Count == 1) return default;
            nr.Count = r.Count - 1;
            return new(nr);
        }
    }

    public readonly ref struct TransientAlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly Transient _t;
        private readonly IAlternateEqualityComparer<TAlt, T> _cmp;

        internal TransientAlternateLookup(Transient t, IAlternateEqualityComparer<TAlt, T> cmp) { _t = t; _cmp = cmp; }

        public bool Contains(TAlt item) => _t.ContainsProbe(new AltProbe<TAlt, T>(item, _cmp));

        /// <summary>Adds by alternate key; <c>T</c> is created only when absent.</summary>
        public bool Add(TAlt item) => _t.AddProbe(new AltProbe<TAlt, T>(item, _cmp));

        public bool Remove(TAlt item) => _t.RemoveProbe(new AltProbe<TAlt, T>(item, _cmp));
    }

    internal T[] ToArrayFast()
    {
        var r = new T[Count];
        var i = 0;
        foreach (var x in this) r[i++] = x;
        return r;
    }

    public Enumerator GetEnumerator() => new(_root);
    internal Cursor GetCursor() => new(_root);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetCursor();
    IEnumerator IEnumerable.GetEnumerator() => GetCursor();

    // Hash-ordered (or holed) storage: no contiguous span and no positional access, so callers enumerate.
    bool IRefEnumerable<T, Enumerator>.TryGetSpan(out ReadOnlySpan<T> span)
    {
        span = default;
        return Count == 0;
    }

    bool IRefEnumerable<T, Enumerator>.TryCopyTo(int index, Span<T> destination)
    {
        RefEnumerable.CopyLength(Count, index, destination.Length);
        return false;
    }

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<T>
    {
        private readonly ChampNode<SetEntry<T>>? _root;
        private ChampIterator<SetEntry<T>> _it;

        internal Cursor(ChampNode<SetEntry<T>>? root) { _root = root; _it = new(root); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _it.MoveNext();

        public readonly ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _it.Current.Key;
        }

        T IEnumerator<T>.Current => Current;
        object IEnumerator.Current => Current;
        public void Reset() => _it = new(_root);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c>, so it can never be boxed or captured, which lets it
    /// walk each trie node's entries as a span (see <c>ChampSpanIterator</c>). The <see cref="IEnumerable{T}"/> path
    /// hands out a separate heap-safe cursor instead (one allocation, as LINQ expects).
    /// </summary>
    public ref struct Enumerator : IEnumerator<T>
    {
        private readonly ChampNode<SetEntry<T>>? _root;
        private ChampSpanIterator<SetEntry<T>> _it;

        internal Enumerator(ChampNode<SetEntry<T>>? root)
        {
            _root = root;
            _it = new(root);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _it.MoveNext();

        public readonly ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _it.Current.Key;
        }

        T IEnumerator<T>.Current => Current;

        readonly object? IEnumerator.Current => Current;
        public void Reset() => _it = new(_root);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Non-escapable mutation handle passed to <see cref="Mutate{TArg}"/> (see <see cref="ImmVector{T}.Scope"/>).
    /// </summary>
    public readonly ref struct Scope
    {
        private readonly Transient _t;
        internal Scope(Transient t) => _t = t;

        public int Count => _t.Count;
        public bool Contains(T item) => _t.Contains(item);
        public bool Add(T item) => _t.Add(item);
        public bool Remove(T item) => _t.Remove(item);
        public void Clear() => _t.Clear();
        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => _t.GetAlternateLookup<TAlt>();
    }

    /// <summary>Mutable view (immer::set_transient). Not thread-safe. Reusable via <see cref="Reset"/>.</summary>
    public sealed class Transient
    {
        private ChampNode<SetEntry<T>>? _root;
        private int _count;
        private Owner? _owner;
        private readonly List<ChampNode<SetEntry<T>>> _slack = [];
        private ChampNode<SetEntry<T>>? _snapshot;

        [ThreadStatic] private static Transient? t_cache;

        public Transient() { }

        internal Transient(ChampNode<SetEntry<T>>? root) => Reset(new(root));

        internal static Transient Rent(ChampNode<SetEntry<T>>? root)
        {
            var t = t_cache;
            if (t is null) t = new Transient();
            else t_cache = null;
            t.Reset(new(root));
            return t;
        }

        internal static void Return(Transient t)
        {
            t.Clear();
            t_cache = t;
        }

        private Owner Token => _owner ??= new ChampOwner<SetEntry<T>>(_slack);

        public int Count => _count;

        public bool Contains(T item) => !Unsafe.IsNullRef(ref Champ<SetEntry<T>, T, DefaultHasher<T>>.Find(_root, item));

        /// <summary>Returns true if added. A no-op (and allocation-free) when already present.</summary>
        public bool Add(T item) => AddProbe(new KeyProbe<T, DefaultHasher<T>>(item, Champ<SetEntry<T>, T, DefaultHasher<T>>.HashOf(item)));

        public bool Remove(T item) => RemoveProbe(new KeyProbe<T, DefaultHasher<T>>(item, Champ<SetEntry<T>, T, DefaultHasher<T>>.HashOf(item)));

        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => new(this, AlternateComparer<TAlt>() ?? throw NoAlternate<TAlt>());

        internal bool ContainsProbe<TP>(scoped TP probe) where TP : IKeyProbe<T>, allows ref struct
            => _root is not null && !Unsafe.IsNullRef(ref Champ<SetEntry<T>, T, DefaultHasher<T>>.FindAt(_root, probe, 0));

        internal bool AddProbe<TP>(scoped TP probe) where TP : IKeyProbe<T>, allows ref struct
        {
            if (ContainsProbe(probe)) return false;
            var o = Token;
            _snapshot = null;
            _root ??= new ChampNode<SetEntry<T>>(0, 0, [], [], o);
            Champ<SetEntry<T>, T, DefaultHasher<T>>.GetOrAdd(ref _root!, probe, o, out _);
            _count++;
            return true;
        }

        internal bool RemoveProbe<TP>(scoped TP probe) where TP : IKeyProbe<T>, allows ref struct
        {
            if (_root is null) return false;
            var removed = false;
            var nr = Champ<SetEntry<T>, T, DefaultHasher<T>>.Remove(_root, probe, 0, Token, ref removed);
            if (!removed) return false;
            _snapshot = null;
            _root = --_count == 0 ? null : nr;
            return true;
        }

        public void Clear()
        {
            _slack.Clear();
            _root = null;
            _count = 0;
            _owner = null;
            _snapshot = null;
        }

        public void Reset(ImmSet<T> source)
        {
            _slack.Clear();
            _root = source._root;
            _count = source.Count;
            _owner = null;
            _snapshot = source._root;
        }

        public ImmSet<T> ToPersistent()
        {
            if (_snapshot is not null || _root is null) return new(_root);
            if (_slack.Count != 0) Champ<SetEntry<T>, T, DefaultHasher<T>>.Trim(_slack);
            if (_owner is not null && ReferenceEquals(_root.Owner, _owner)) _root.Count = _count;
            _owner = null;
            _snapshot = _root;
            return new(_root);
        }

        public Enumerator GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

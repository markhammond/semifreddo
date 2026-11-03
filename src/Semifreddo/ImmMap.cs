using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

public static class ImmMap
{
    public static ImmMap<K, V> Create<K, V>(ReadOnlySpan<KeyValuePair<K, V>> items) where K : notnull
        => ImmMap<K, V>.Empty.SetItems(items);

    public static ImmMap<K, V> CreateRange<K, V>(IEnumerable<KeyValuePair<K, V>> items) where K : notnull
        => ImmMap<K, V>.Empty.SetItems(items);

    public static ImmMap<K, V>.Transient CreateTransient<K, V>() where K : notnull => new();
}

/// <summary>
/// Persistent hash map (immer::map) backed by a CHAMP trie. O(log32 n) lookup/insert/remove.
/// A single-reference readonly struct: tear-free, CAS-able, <c>default</c> is empty, freely shareable across threads.
/// Equality is by version, not contents: two maps are equal only when they are the same version
/// (<see cref="IsSameAs"/>); compare contents explicitly.
/// </summary>
public readonly struct ImmMap<K, V> : IReadOnlyDictionary<K, V>, IRefEnumerable<KeyValuePair<K, V>, ImmMap<K, V>.Enumerator>, IImmutable where K : notnull
{
    private readonly ChampNode<MapEntry<K, V>>? _root;

    internal ImmMap(ChampNode<MapEntry<K, V>>? root) => _root = root;

    internal ChampNode<MapEntry<K, V>>? Root => _root;

    public static ImmMap<K, V> Empty => default;

    public int Count => _root is null ? 0 : _root.Count;

    public bool IsEmpty => _root is null;

    public bool IsSameAs(ImmMap<K, V> other) => ReferenceEquals(_root, other._root);

    public V this[K key]
    {
        get
        {
            ref readonly var v = ref GetValueRefOrNullRef(key);
            if (Unsafe.IsNullRef(in v)) Throw.KeyNotFound(key);
            return v;
        }
    }

    /// <summary>Zero-copy lookup; test the result with <see cref="Unsafe.IsNullRef{T}(ref readonly T)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly V GetValueRefOrNullRef(K key)
    {
        ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key);
        if (Unsafe.IsNullRef(ref e)) return ref Unsafe.NullRef<V>();
        return ref e.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value)
    {
        ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key);
        if (Unsafe.IsNullRef(ref e)) { value = default; return false; }
        value = e.Value;
        return true;
    }

    public V? GetValueOrDefault(K key, V? defaultValue = default)
        => TryGetValue(key, out var v) ? v : defaultValue;

    public bool ContainsKey(K key) => !Unsafe.IsNullRef(ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key));

    public ImmMap<K, V> SetItem(K key, V value)
    {
        var e = new MapEntry<K, V>(key, value);
        var hash = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.HashOf(key);
        var r = _root;
        if (r is null)
        {
            var s = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Singleton(in e, hash, null);
            s.Count = 1;
            return new(s);
        }
        var added = false;
        var nr = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Set(r, in e, key, hash, 0, null, ref added);
        nr.Count = r.Count + (added ? 1 : 0); // nr is always a fresh node in persistent mode
        return new(nr);
    }

    public ImmMap<K, V> SetItems(ReadOnlySpan<KeyValuePair<K, V>> items)
    {
        if (items.IsEmpty) return this;
        var t = Transient.Rent(_root);
        try
        {
            foreach (ref readonly var kv in items) t[kv.Key] = kv.Value;
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmMap<K, V> SetItems(IEnumerable<KeyValuePair<K, V>> items)
    {
        var t = Transient.Rent(_root);
        try
        {
            foreach (var kv in items) t[kv.Key] = kv.Value;
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    /// <summary>immer::map::update. <paramref name="update"/> receives the existing value or <c>default</c>.</summary>
    public ImmMap<K, V> Update<TArg>(K key, TArg arg, Func<V?, bool, TArg, V> update) where TArg : allows ref struct
    {
        ref readonly var cur = ref GetValueRefOrNullRef(key);
        var exists = !Unsafe.IsNullRef(in cur);
        return SetItem(key, update(exists ? cur : default, exists, arg));
    }

    public ImmMap<K, V> AddOrUpdate(K key, V addValue, Func<K, V, V> update)
        => TryGetValue(key, out var v) ? SetItem(key, update(key, v)) : SetItem(key, addValue);

    public ImmMap<K, V> Remove(K key)
    {
        var r = _root;
        if (r is null) return this;
        var removed = false;
        var nr = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Remove(
            r, key, Champ<MapEntry<K, V>, K, DefaultHasher<K>>.HashOf(key), 0, null, ref removed);
        if (!removed) return this;
        if (r.Count == 1) return default;
        nr.Count = r.Count - 1;
        return new(nr);
    }

    /// <summary>Batch of in-place mutations through a non-escapable <see cref="Scope"/> over a thread-local cached transient.</summary>
    public ImmMap<K, V> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(_root);
        try { batch(new Scope(t), arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmMap<K, V> Mutate(Action<Scope> batch)
    {
        var t = Transient.Rent(_root);
        try { batch(new Scope(t)); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public Transient ToTransient() => new(_root);

    internal static IAlternateEqualityComparer<TAlt, K>? AlternateComparer<TAlt>() where TAlt : notnull, allows ref struct
        => EqualityComparer<K>.Default as IAlternateEqualityComparer<TAlt, K>;

    internal static InvalidOperationException NoAlternate<TAlt>() where TAlt : allows ref struct
        => new($"EqualityComparer<{typeof(K).Name}>.Default does not implement IAlternateEqualityComparer<{typeof(TAlt).Name}, {typeof(K).Name}>.");

    /// <summary>
    /// Lookups by an alternate key type (cf. <c>Dictionary.GetAlternateLookup</c>), e.g. <c>ReadOnlySpan&lt;char&gt;</c>
    /// for string keys: no key allocation per probe. Requires <c>EqualityComparer&lt;K&gt;.Default</c> to implement
    /// <c>IAlternateEqualityComparer&lt;TAlt, K&gt;</c>.
    /// </summary>
    public AlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
        => TryGetAlternateLookup<TAlt>(out var l) ? l : throw NoAlternate<TAlt>();

    public bool TryGetAlternateLookup<TAlt>(out AlternateLookup<TAlt> lookup) where TAlt : notnull, allows ref struct
    {
        var c = AlternateComparer<TAlt>();
        lookup = c is null ? default : new(_root, c);
        return c is not null;
    }

    /// <summary>Alternate-key view of one map version.</summary>
    public readonly struct AlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly ChampNode<MapEntry<K, V>>? _root;
        private readonly IAlternateEqualityComparer<TAlt, K> _cmp;

        internal AlternateLookup(ChampNode<MapEntry<K, V>>? root, IAlternateEqualityComparer<TAlt, K> cmp) { _root = root; _cmp = cmp; }

        public ImmMap<K, V> Map => new(_root);

        private ref MapEntry<K, V> Find(TAlt key)
        {
            if (_root is null) return ref Unsafe.NullRef<MapEntry<K, V>>();
            return ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.FindAt(_root, new AltProbe<TAlt, K>(key, _cmp), 0);
        }

        public bool ContainsKey(TAlt key) => !Unsafe.IsNullRef(ref Find(key));

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out V value)
        {
            ref var e = ref Find(key);
            if (Unsafe.IsNullRef(ref e)) { value = default; return false; }
            value = e.Value;
            return true;
        }

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out K actualKey, [MaybeNullWhen(false)] out V value)
        {
            ref var e = ref Find(key);
            if (Unsafe.IsNullRef(ref e)) { actualKey = default; value = default; return false; }
            actualKey = e.Key;
            value = e.Value;
            return true;
        }

        public V this[TAlt key]
        {
            get
            {
                if (!TryGetValue(key, out var v)) Throw.KeyNotFound();
                return v;
            }
        }

        /// <summary>Persistent remove by alternate key (no K allocation).</summary>
        public ImmMap<K, V> Remove(TAlt key)
        {
            var r = _root;
            if (r is null) return default;
            var removed = false;
            var nr = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Remove(r, new AltProbe<TAlt, K>(key, _cmp), 0, null, ref removed);
            if (!removed) return new(r);
            if (r.Count == 1) return default;
            nr.Count = r.Count - 1;
            return new(nr);
        }
    }

    /// <summary>Alternate-key view of a transient / <see cref="Scope"/>. A ref struct, so it cannot outlive the batch.</summary>
    public readonly ref struct TransientAlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly Transient _t;
        private readonly IAlternateEqualityComparer<TAlt, K> _cmp;

        internal TransientAlternateLookup(Transient t, IAlternateEqualityComparer<TAlt, K> cmp) { _t = t; _cmp = cmp; }

        public bool ContainsKey(TAlt key) => !Unsafe.IsNullRef(ref _t.FindEntry(new AltProbe<TAlt, K>(key, _cmp)));

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out V value)
        {
            ref var e = ref _t.FindEntry(new AltProbe<TAlt, K>(key, _cmp));
            if (Unsafe.IsNullRef(ref e)) { value = default; return false; }
            value = e.Value;
            return true;
        }

        /// <summary>Upsert by alternate key; <c>K</c> is created (<c>comparer.Create</c>) only if the key is new.</summary>
        public ref V GetValueRefOrAddDefault(TAlt key, out bool exists)
            => ref _t.GetOrAddEntry(new AltProbe<TAlt, K>(key, _cmp), out exists).Value;

        public V this[TAlt key]
        {
            get
            {
                if (!TryGetValue(key, out var v)) Throw.KeyNotFound();
                return v;
            }
            set => GetValueRefOrAddDefault(key, out _) = value;
        }

        public bool Remove(TAlt key) => _t.RemoveProbe(new AltProbe<TAlt, K>(key, _cmp));
    }

    public Enumerator GetEnumerator() => new(_root);
    internal Cursor GetCursor() => new(_root);
    IEnumerator<KeyValuePair<K, V>> IEnumerable<KeyValuePair<K, V>>.GetEnumerator() => GetCursor();
    IEnumerator IEnumerable.GetEnumerator() => GetCursor();

    // Hash-ordered (or holed) storage: no contiguous span and no positional access, so callers enumerate.
    bool IRefEnumerable<KeyValuePair<K, V>, Enumerator>.TryGetSpan(out ReadOnlySpan<KeyValuePair<K, V>> span)
    {
        span = default;
        return Count == 0;
    }

    bool IRefEnumerable<KeyValuePair<K, V>, Enumerator>.TryCopyTo(int index, Span<KeyValuePair<K, V>> destination)
    {
        RefEnumerable.CopyLength(Count, index, destination.Length);
        return false;
    }

    public IEnumerable<K> Keys => KeysOf(this);
    public IEnumerable<V> Values => ValuesOf(this);

    // Iterators cannot hold the ref struct enumerator across yield; the heap-safe cursor can.
    private static IEnumerable<K> KeysOf(ImmMap<K, V> m) { var c = m.GetCursor(); while (c.MoveNext()) yield return c.CurrentKey; }
    private static IEnumerable<V> ValuesOf(ImmMap<K, V> m) { var c = m.GetCursor(); while (c.MoveNext()) yield return c.CurrentValue; }

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<KeyValuePair<K, V>>
    {
        private readonly ChampNode<MapEntry<K, V>>? _root;
        private ChampIterator<MapEntry<K, V>> _it;

        internal Cursor(ChampNode<MapEntry<K, V>>? root) { _root = root; _it = new(root); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _it.MoveNext();

        public readonly KeyValuePair<K, V> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ref var e = ref _it.Current; return new(e.Key, e.Value); }
        }

        /// <summary>Key of the current entry without constructing a pair.</summary>
        public readonly ref readonly K CurrentKey => ref _it.Current.Key;

        /// <summary>Value of the current entry by reference (no copy for large structs).</summary>
        public readonly ref readonly V CurrentValue => ref _it.Current.Value;

        object IEnumerator.Current => Current;
        public void Reset() => _it = new(_root);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c>, so it can never be boxed or captured, which lets it
    /// walk each trie node's entries as a span (see <c>ChampSpanIterator</c>). The <see cref="IEnumerable{T}"/> path
    /// hands out a separate heap-safe cursor instead (one allocation, as LINQ expects).
    /// </summary>
    public ref struct Enumerator : IEnumerator<KeyValuePair<K, V>>, IDisposable
    {
        private readonly ChampNode<MapEntry<K, V>>? _root;
        private ChampSpanIterator<MapEntry<K, V>> _it;

        internal Enumerator(ChampNode<MapEntry<K, V>>? root)
        {
            _root = root;
            _it = new(root);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _it.MoveNext();

        public readonly KeyValuePair<K, V> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ref readonly var e = ref _it.Current; return new(e.Key, e.Value); }
        }

        /// <summary>Key of the current entry without constructing a pair.</summary>
        public readonly ref readonly K CurrentKey => ref _it.Current.Key;

        /// <summary>Value of the current entry by reference (no copy for large structs).</summary>
        public readonly ref readonly V CurrentValue => ref _it.Current.Value;

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
        public V this[K key] { get => _t[key]; set => _t[key] = value; }
        public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) => _t.TryGetValue(key, out value);
        public bool ContainsKey(K key) => _t.ContainsKey(key);
        /// <inheritdoc cref="Transient.GetValueRefOrAddDefault"/>
        public ref V GetValueRefOrAddDefault(K key, out bool exists) => ref _t.GetValueRefOrAddDefault(key, out exists);
        public bool SetItem(K key, V value) => _t.SetItem(key, value);
        public bool TryAdd(K key, V value) => _t.TryAdd(key, value);
        public bool Remove(K key) => _t.Remove(key);
        public void Clear() => _t.Clear();
        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => _t.GetAlternateLookup<TAlt>();
    }

    /// <summary>
    /// Mutable view (immer::map_transient). Not thread-safe. Reusable via <see cref="Reset"/>.
    /// </summary>
    public sealed class Transient
    {
        private ChampNode<MapEntry<K, V>>? _root;
        private int _count;
        private Owner? _owner;
        private readonly List<ChampNode<MapEntry<K, V>>> _slack = [];
        private ChampNode<MapEntry<K, V>>? _snapshot;

        [ThreadStatic] private static Transient? t_cache;

        public Transient() { }

        internal Transient(ChampNode<MapEntry<K, V>>? root) => Reset(new(root));

        internal static Transient Rent(ChampNode<MapEntry<K, V>>? root)
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

        private Owner Token => _owner ??= new ChampOwner<MapEntry<K, V>>(_slack);

        public int Count => _count;

        public V this[K key]
        {
            get
            {
                ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key);
                if (Unsafe.IsNullRef(ref e)) Throw.KeyNotFound(key);
                return e.Value;
            }
            set => GetValueRefOrAddDefault(key, out _) = value;
        }

        public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value)
        {
            ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key);
            if (Unsafe.IsNullRef(ref e)) { value = default; return false; }
            value = e.Value;
            return true;
        }

        public bool ContainsKey(K key) => !Unsafe.IsNullRef(ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key));

        /// <summary>Read-only reference to a value (no path copy); test with <c>Unsafe.IsNullRef</c>. Invalidated by
        /// the next mutation; never write through it (the node may be shared with a persistent version).</summary>
        public ref readonly V GetValueRefOrNullRef(K key)
        {
            ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Find(_root, key);
            if (Unsafe.IsNullRef(ref e)) return ref Unsafe.NullRef<V>();
            return ref e.Value;
        }

        /// <summary>
        /// Single-pass upsert returning a mutable reference to the value slot (cf. CollectionsMarshal).
        /// After the first touch of a path this is allocation-free, e.g. <c>t.GetValueRefOrAddDefault(k, out _) += qty;</c>.
        /// The ref is invalidated by any other mutation or by <see cref="ToPersistent"/>.
        /// </summary>
        public ref V GetValueRefOrAddDefault(K key, out bool exists)
            => ref GetOrAddEntry(new KeyProbe<K, DefaultHasher<K>>(key, Champ<MapEntry<K, V>, K, DefaultHasher<K>>.HashOf(key)), out exists).Value;

        internal ref MapEntry<K, V> GetOrAddEntry<TP>(scoped TP probe, out bool exists) where TP : IKeyProbe<K>, allows ref struct
        {
            var o = Token;
            _snapshot = null;
            _root ??= new ChampNode<MapEntry<K, V>>(0, 0, [], [], o);
            ref var e = ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.GetOrAdd(ref _root!, probe, o, out exists);
            if (!exists) _count++;
            return ref e;
        }

        internal ref MapEntry<K, V> FindEntry<TP>(scoped TP probe) where TP : IKeyProbe<K>, allows ref struct
        {
            if (_root is null) return ref Unsafe.NullRef<MapEntry<K, V>>();
            return ref Champ<MapEntry<K, V>, K, DefaultHasher<K>>.FindAt(_root, probe, 0);
        }

        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => new(this, AlternateComparer<TAlt>() ?? throw NoAlternate<TAlt>());

        /// <summary>Adds or replaces. Returns true if the key was added.</summary>
        public bool SetItem(K key, V value)
        {
            GetValueRefOrAddDefault(key, out var exists) = value;
            return !exists;
        }

        public bool TryAdd(K key, V value)
        {
            if (ContainsKey(key)) return false;   // probe first: an existing key copies no path and keeps the version
            ref var slot = ref GetValueRefOrAddDefault(key, out var exists);
            if (exists) return false;
            slot = value;
            return true;
        }

        public bool Remove(K key) => RemoveProbe(new KeyProbe<K, DefaultHasher<K>>(key, Champ<MapEntry<K, V>, K, DefaultHasher<K>>.HashOf(key)));

        internal bool RemoveProbe<TP>(scoped TP probe) where TP : IKeyProbe<K>, allows ref struct
        {
            if (_root is null) return false;
            var removed = false;
            var nr = Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Remove(_root, probe, 0, Token, ref removed);
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

        public void Reset(ImmMap<K, V> source)
        {
            _slack.Clear();
            _root = source._root;
            _count = source.Count;
            _owner = null;
            _snapshot = source._root;
        }

        /// <summary>Freezes in time proportional to the nodes this batch grew (it trims their spare capacity), never
        /// walking the rest. The transient remains usable; later writes copy-on-write.</summary>
        public ImmMap<K, V> ToPersistent()
        {
            if (_snapshot is not null || _root is null) return new(_root);
            if (_slack.Count != 0) Champ<MapEntry<K, V>, K, DefaultHasher<K>>.Trim(_slack);
            if (_owner is not null && ReferenceEquals(_root.Owner, _owner)) _root.Count = _count;
            _owner = null;
            _snapshot = _root;
            return new(_root);
        }

        public Enumerator GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

public static class ImmOrderedMap
{
    public static ImmOrderedMap<K, V> CreateRange<K, V>(IEnumerable<KeyValuePair<K, V>> items) where K : notnull
        => ImmOrderedMap<K, V>.Empty.Mutate(items, static (s, xs) => { foreach (var kv in xs) s[kv.Key] = kv.Value; });

    public static ImmOrderedMap<K, V>.Transient CreateTransient<K, V>() where K : notnull => new();
}

internal struct OSlot<K, V>
{
    public K Key;
    public V Value;
    public bool Live;

    public OSlot(K key, V value) { Key = key; Value = value; Live = true; }
}

internal sealed class OHead<K, V> where K : notnull
{
    public ImmFlexVector<OSlot<K, V>> Slots; // insertion order; ends are always live
    public ImmMap<K, long> Index;            // key -> absolute slot id (position = id - Base)
    public long Base;
    public int Dead;                         // interior tombstones
}

/// <summary>
/// Persistent insertion-ordered dictionary (cf. <c>OrderedDictionary&lt;K,V&gt;</c>, Python's compact dict).
/// <list type="bullet">
/// <item>Lookup / update-in-place: O(log32 n) — one CHAMP lookup plus one RRB index.</item>
/// <item>Append (new key): amortised O(1) + O(log32 n). Iteration: in insertion order, span-chunked, no hashing.</item>
/// <item>Remove: O(log n). Removing the first/last entry (FIFO/LIFO use) trims immediately via RRB Skip/Take;
/// interior removals leave a tombstone, compacted (O(n)) once there are more than 32 tombstones and they outnumber the
/// live entries.
/// Note the amortisation is per lineage: many branches all forked just below the threshold each pay for compaction.</item>
/// </list>
/// </summary>
public readonly struct ImmOrderedMap<K, V> : IReadOnlyDictionary<K, V>, IRefEnumerable<KeyValuePair<K, V>, ImmOrderedMap<K, V>.Enumerator>, IImmutable where K : notnull
{
    private const int CompactMin = 32;

    private readonly OHead<K, V>? _h;

    internal ImmOrderedMap(OHead<K, V>? h) => _h = h;

    internal OHead<K, V>? Head => _h;

    public static ImmOrderedMap<K, V> Empty => default;

    public int Count => _h is null ? 0 : _h.Index.Count;

    public bool IsEmpty => _h is null;

    public bool IsSameAs(ImmOrderedMap<K, V> other) => ReferenceEquals(_h, other._h);

    public V this[K key]
    {
        get
        {
            ref readonly var v = ref GetValueRefOrNullRef(key);
            if (Unsafe.IsNullRef(in v)) Throw.KeyNotFound(key);
            return v;
        }
    }

    public ref readonly V GetValueRefOrNullRef(K key)
    {
        var h = _h;
        if (h is null) return ref Unsafe.NullRef<V>();
        ref readonly var id = ref h.Index.GetValueRefOrNullRef(key);
        if (Unsafe.IsNullRef(in id)) return ref Unsafe.NullRef<V>();
        return ref h.Slots[(int)(id - h.Base)].Value;
    }

    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value)
    {
        ref readonly var v = ref GetValueRefOrNullRef(key);
        if (Unsafe.IsNullRef(in v)) { value = default; return false; }
        value = v;
        return true;
    }

    public bool ContainsKey(K key) => _h is not null && _h.Index.ContainsKey(key);

    /// <summary>Oldest entry.</summary>
    public KeyValuePair<K, V> First
    {
        get
        {
            if (_h is null) Throw.Empty();
            ref readonly var s = ref _h.Slots.First;
            return new(s.Key, s.Value);
        }
    }

    /// <summary>Newest entry.</summary>
    public KeyValuePair<K, V> Last
    {
        get
        {
            if (_h is null) Throw.Empty();
            ref readonly var s = ref _h.Slots.Last;
            return new(s.Key, s.Value);
        }
    }

    /// <summary>Adds at the end, or replaces the value of an existing key keeping its position.</summary>
    public ImmOrderedMap<K, V> SetItem(K key, V value)
    {
        var h = _h;
        var slot = new OSlot<K, V>(key, value);
        if (h is null)
            return new(new OHead<K, V> { Slots = ImmFlexVector<OSlot<K, V>>.Empty.Add(slot), Index = ImmMap<K, long>.Empty.SetItem(key, 0) });

        if (h.Index.TryGetValue(key, out var id))
        {
            var pos = (int)(id - h.Base);
            slot = new OSlot<K, V>(h.Slots[pos].Key, value);   // the stored key stays, as in the index (and Dictionary)
            return new(new OHead<K, V> { Slots = h.Slots.SetItem(pos, slot), Index = h.Index, Base = h.Base, Dead = h.Dead });
        }

        return new(new OHead<K, V>
        {
            Slots = h.Slots.Add(slot),
            Index = h.Index.SetItem(key, h.Base + h.Slots.Count),
            Base = h.Base,
            Dead = h.Dead,
        });
    }

    public ImmOrderedMap<K, V> Remove(K key)
    {
        var h = _h;
        if (h is null || !h.Index.TryGetValue(key, out var id)) return this;
        return RemoveSlot(h, id, h.Index.Remove(key));
    }

    private static ImmOrderedMap<K, V> RemoveSlot(OHead<K, V> h, long id, ImmMap<K, long> index)
    {
        if (index.IsEmpty) return default;

        var slots = h.Slots;
        var b = h.Base;
        int dead = h.Dead, pos = (int)(id - b);
        if (pos == 0)
        {
            var j = 1;
            while (!slots[j].Live) j++;
            dead -= j - 1;
            slots = slots.Skip(j);
            b += j;
        }
        else if (pos == slots.Count - 1)
        {
            var j = pos - 1;
            while (!slots[j].Live) j--;
            dead -= pos - 1 - j;
            slots = slots.Take(j + 1);
        }
        else
        {
            slots = slots.SetItem(pos, default); // tombstone; also releases Key/Value references
            dead++;
        }

        var r = new OHead<K, V> { Slots = slots, Index = index, Base = b, Dead = dead };
        if (dead > CompactMin && dead > index.Count) Compact(r);
        return new(r);
    }

    private static void Compact(OHead<K, V> h)
    {
        var slots = ImmFlexVector.CreateTransient<OSlot<K, V>>();
        var index = h.Index.ToTransient();
        foreach (ref readonly var s in h.Slots)
        {
            if (!s.Live) continue;
            index[s.Key] = slots.Count;
            slots.Add(s);
        }
        h.Slots = slots.ToPersistent();
        h.Index = index.ToPersistent();
        h.Base = 0;
        h.Dead = 0;
    }

    public ImmOrderedMap<K, V> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(_h);
        try { batch(new Scope(t), arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmOrderedMap<K, V> Mutate(Action<Scope> batch)
    {
        var t = Transient.Rent(_h);
        try { batch(new Scope(t)); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    /// <summary>Lookups by an alternate key type (cf. <c>OrderedDictionary.GetAlternateLookup</c>).</summary>
    public AlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
        => TryGetAlternateLookup<TAlt>(out var l) ? l : throw ImmMap<K, long>.NoAlternate<TAlt>();

    public bool TryGetAlternateLookup<TAlt>(out AlternateLookup<TAlt> lookup) where TAlt : notnull, allows ref struct
    {
        var ok = (_h?.Index ?? default).TryGetAlternateLookup<TAlt>(out var ix);
        lookup = ok ? new(_h, ix) : default;
        return ok;
    }

    public readonly struct AlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly OHead<K, V>? _h;
        private readonly ImmMap<K, long>.AlternateLookup<TAlt> _ix;

        internal AlternateLookup(OHead<K, V>? h, ImmMap<K, long>.AlternateLookup<TAlt> ix) { _h = h; _ix = ix; }

        public ImmOrderedMap<K, V> Map => new(_h);

        public bool ContainsKey(TAlt key) => _ix.ContainsKey(key);

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out V value) => TryGetValue(key, out _, out value);

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out K actualKey, [MaybeNullWhen(false)] out V value)
        {
            if (_h is null || !_ix.TryGetValue(key, out var id)) { actualKey = default; value = default; return false; }
            ref readonly var s = ref _h.Slots[(int)(id - _h.Base)];
            actualKey = s.Key;
            value = s.Value;
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

        public ImmOrderedMap<K, V> Remove(TAlt key)
        {
            if (_h is null || !_ix.TryGetValue(key, out var id)) return new(_h);
            return RemoveSlot(_h, id, _ix.Remove(key));
        }
    }

    public readonly ref struct TransientAlternateLookup<TAlt> where TAlt : notnull, allows ref struct
    {
        private readonly Transient _t;
        private readonly IAlternateEqualityComparer<TAlt, K> _cmp;

        internal TransientAlternateLookup(Transient t, IAlternateEqualityComparer<TAlt, K> cmp) { _t = t; _cmp = cmp; }

        public bool ContainsKey(TAlt key) => !Unsafe.IsNullRef(ref _t.Index.FindEntry(new AltProbe<TAlt, K>(key, _cmp)));

        public bool TryGetValue(TAlt key, [MaybeNullWhen(false)] out V value) => _t.TryGetCore(new AltProbe<TAlt, K>(key, _cmp), out value);

        /// <summary>Upsert by alternate key; <c>K</c> is created once (only if new) and shared by index and slot.</summary>
        public ref V GetValueRefOrAddDefault(TAlt key, out bool exists) => ref _t.GetOrAddCore(new AltProbe<TAlt, K>(key, _cmp), out exists);

        public V this[TAlt key]
        {
            get
            {
                if (!TryGetValue(key, out var v)) Throw.KeyNotFound();
                return v;
            }
            set => GetValueRefOrAddDefault(key, out _) = value;
        }

        public bool Remove(TAlt key) => _t.RemoveCore(new AltProbe<TAlt, K>(key, _cmp));
    }

    public Transient ToTransient()
    {
        var t = new Transient();
        t.Reset(this);
        return t;
    }

    public Enumerator GetEnumerator() => new(_h);
    internal Cursor GetCursor() => new(_h);
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
    private static IEnumerable<K> KeysOf(ImmOrderedMap<K, V> m) { var c = m.GetCursor(); while (c.MoveNext()) yield return c.CurrentKey; }
    private static IEnumerable<V> ValuesOf(ImmOrderedMap<K, V> m) { var c = m.GetCursor(); while (c.MoveNext()) yield return c.CurrentValue; }

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<KeyValuePair<K, V>>
    {
        private readonly OHead<K, V>? _h;
        private ImmFlexVector<OSlot<K, V>>.Cursor _e;

        internal Cursor(OHead<K, V>? h)
        {
            _h = h;
            _e = (h?.Slots ?? default).GetCursor();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (_e.MoveNext())
                if (_e.Current.Live) return true;
            return false;
        }

        public readonly KeyValuePair<K, V> Current
        {
            get { ref readonly var s = ref _e.Current; return new(s.Key, s.Value); }
        }

        public readonly ref readonly K CurrentKey => ref _e.Current.Key;
        public readonly ref readonly V CurrentValue => ref _e.Current.Value;

        object IEnumerator.Current => Current;
        public void Reset() => this = new(_h);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator in insertion order: a <c>ref struct</c> over the slot vector's span-based
    /// enumerator, so tombstones are skipped within the current leaf span. It can never be boxed; the
    /// <see cref="IEnumerable{T}"/> path hands out a separate heap-safe cursor instead.
    /// </summary>
    public ref struct Enumerator : IEnumerator<KeyValuePair<K, V>>
    {
        private ImmFlexVector<OSlot<K, V>>.Enumerator _e;

        internal Enumerator(OHead<K, V>? h) => _e = (h?.Slots ?? default).GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            while (_e.MoveNext())
                if (_e.Current.Live) return true;
            return false;
        }

        public readonly KeyValuePair<K, V> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { ref readonly var s = ref _e.Current; return new(s.Key, s.Value); }
        }

        /// <summary>Key of the current entry without constructing a pair.</summary>
        public readonly ref readonly K CurrentKey => ref _e.Current.Key;

        /// <summary>Value of the current entry by reference (no copy for large structs).</summary>
        public readonly ref readonly V CurrentValue => ref _e.Current.Value;

        readonly object? IEnumerator.Current => Current;
        public void Reset() => _e.Reset();
        public readonly void Dispose() { }
    }

    /// <summary>Non-escapable mutation handle passed to <see cref="Mutate{TArg}"/> (see <see cref="ImmVector{T}.Scope"/>).</summary>
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
        public bool Remove(K key) => _t.Remove(key);
        public void Clear() => _t.Clear();
        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => _t.GetAlternateLookup<TAlt>();
    }

    /// <summary>Mutable view; both underlying structures are transients, so batches are mostly in place. Not thread-safe.</summary>
    public sealed class Transient
    {
        private readonly ImmFlexVector<OSlot<K, V>>.Transient _slots = new();
        private readonly ImmMap<K, long>.Transient _index = new();
        private long _base;
        private int _dead;
        private OHead<K, V>? _last;

        [ThreadStatic] private static Transient? t_cache;

        internal static Transient Rent(OHead<K, V>? head)
        {
            var t = t_cache;
            if (t is null) t = new Transient();
            else t_cache = null;
            t.Reset(new(head));
            return t;
        }

        internal static void Return(Transient t)
        {
            t.Clear();
            t_cache = t;
        }

        public int Count => _index.Count;

        public V this[K key]
        {
            get
            {
                if (!TryGetValue(key, out var v)) Throw.KeyNotFound(key);
                return v;
            }
            set => GetValueRefOrAddDefault(key, out _) = value;
        }

        internal ImmMap<K, long>.Transient Index => _index;

        public TransientAlternateLookup<TAlt> GetAlternateLookup<TAlt>() where TAlt : notnull, allows ref struct
            => new(this, ImmMap<K, long>.AlternateComparer<TAlt>() ?? throw ImmMap<K, long>.NoAlternate<TAlt>());

        public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value)
            => TryGetCore(new KeyProbe<K, DefaultHasher<K>>(key, Champ<MapEntry<K, long>, K, DefaultHasher<K>>.HashOf(key)), out value);

        internal bool TryGetCore<TP>(scoped TP probe, [MaybeNullWhen(false)] out V value) where TP : IKeyProbe<K>, allows ref struct
        {
            ref var e = ref _index.FindEntry(probe);
            if (Unsafe.IsNullRef(ref e)) { value = default; return false; }
            value = _slots[(int)(e.Value - _base)].Value;
            return true;
        }

        public bool ContainsKey(K key) => _index.ContainsKey(key);

        /// <summary>
        /// Single-pass upsert returning a mutable ref to the value (new keys are appended). Invalidated by any other
        /// mutation or <see cref="ToPersistent"/>.
        /// </summary>
        public ref V GetValueRefOrAddDefault(K key, out bool exists)
            => ref GetOrAddCore(new KeyProbe<K, DefaultHasher<K>>(key, Champ<MapEntry<K, long>, K, DefaultHasher<K>>.HashOf(key)), out exists);

        internal ref V GetOrAddCore<TP>(scoped TP probe, out bool exists) where TP : IKeyProbe<K>, allows ref struct
        {
            ref var e = ref _index.GetOrAddEntry(probe, out exists);
            if (!exists)
            {
                e.Value = _base + _slots.Count;
                _slots.Add(new OSlot<K, V>(e.Key, default!)); // the key materialised by the index probe
                return ref _slots.ItemRef(_slots.Count - 1).Value;
            }
            return ref _slots.ItemRef((int)(e.Value - _base)).Value;
        }

        public bool Remove(K key) => RemoveCore(new KeyProbe<K, DefaultHasher<K>>(key, Champ<MapEntry<K, long>, K, DefaultHasher<K>>.HashOf(key)));

        internal bool RemoveCore<TP>(scoped TP probe) where TP : IKeyProbe<K>, allows ref struct
        {
            ref var e = ref _index.FindEntry(probe);
            if (Unsafe.IsNullRef(ref e)) return false;
            var id = e.Value;
            _index.RemoveProbe(probe);
            if (_index.Count == 0)
            {
                _slots.Clear();
                _dead = 0;
                return true;
            }

            var pos = (int)(id - _base);
            if (pos == 0)
            {
                var j = 1;
                while (!_slots[j].Live) j++;
                _dead -= j - 1;
                _slots.Skip(j);
                _base += j;
            }
            else if (pos == _slots.Count - 1)
            {
                var j = pos - 1;
                while (!_slots[j].Live) j--;
                _dead -= pos - 1 - j;
                _slots.Take(j + 1);
            }
            else
            {
                _slots.ItemRef(pos) = default;
                _dead++;
            }
            if (_dead > CompactMin && _dead > _index.Count) Compact();
            return true;
        }

        private void Compact()
        {
            var old = _slots.ToPersistent();
            _slots.Clear();
            foreach (ref readonly var s in old)
            {
                if (!s.Live) continue;
                _index[s.Key] = _slots.Count;
                _slots.Add(s);
            }
            _base = 0;
            _dead = 0;
        }

        public void Clear()
        {
            _slots.Clear();
            _index.Clear();
            _base = 0;
            _dead = 0;
            _last = null;
        }

        public void Reset(ImmOrderedMap<K, V> source)
        {
            var h = source._h;
            _last = h;
            if (h is null) { Clear(); return; }
            _slots.Reset(h.Slots);
            _index.Reset(h.Index);
            _base = h.Base;
            _dead = h.Dead;
        }

        public ImmOrderedMap<K, V> ToPersistent()
        {
            var slots = _slots.ToPersistent();
            var index = _index.ToPersistent();
            if (index.IsEmpty) return default;
            var l = _last;
            if (l is not null && l.Slots.IsSameAs(slots) && l.Index.IsSameAs(index) && l.Base == _base) return new(l);
            return new(_last = new OHead<K, V> { Slots = slots, Index = index, Base = _base, Dead = _dead });
        }

        public Enumerator GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

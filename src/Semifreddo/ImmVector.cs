using System.Collections;
using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

public static class ImmVector
{
    /// <summary>Collection-expression builder: <c>ImmVector&lt;int&gt; v = [1, 2, 3];</c></summary>
    public static ImmVector<T> Create<T>(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return default;
        var t = ImmVector<T>.Transient.Rent(null);
        try { t.AddRange(items); return t.ToPersistent(); }
        finally { ImmVector<T>.Transient.Return(t); }
    }

    public static ImmVector<T> CreateRange<T>(IEnumerable<T> items) => ImmVector<T>.Empty.AddRange(items);

    public static ImmVector<T>.Transient CreateTransient<T>() => new();
}

/// <summary>
/// Persistent vector (immer::vector). O(log32 n) indexed get/set, amortised O(1) append/pop-back.
/// A single-reference readonly struct: copies are atomic, <c>default</c> is the empty vector, and values
/// are safe to share across threads without synchronisation.
/// </summary>
[CollectionBuilder(typeof(ImmVector), nameof(ImmVector.Create))]
public readonly struct ImmVector<T> : IReadOnlyList<T>, IRefEnumerable<T, ImmVector<T>.Enumerator>, IImmutable
{
    internal readonly VHead<T>? Head;

    internal ImmVector(VHead<T>? head) => Head = head;

    public static ImmVector<T> Empty => default;

    public int Count => Head is null ? 0 : Head.Count;

    public bool IsEmpty => Head is null;

    /// <summary>True if both are the same version (O(1) identity check; implies sequence equality).</summary>
    public bool IsSameAs(ImmVector<T> other) => ReferenceEquals(Head, other.Head);

    public ref readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var h = Head;
            if (h is null || (uint)index >= (uint)h.Count) Throw.IndexOutOfRange();
            if (index >= VTree<T>.TailOffset(h.Count)) return ref h.Tail.Items[index & VTree<T>.Mask];
            return ref VTree<T>.LeafFor(h.Root!, h.Shift, index).Items[index & VTree<T>.Mask];
        }
    }

    T IReadOnlyList<T>.this[int index] => this[index];

    public ref readonly T Last
    {
        get
        {
            var h = Head;
            if (h is null) Throw.Empty();
            return ref h.Tail.Items[(h.Count - 1) & VTree<T>.Mask];
        }
    }

    public ImmVector<T> Add(T item)
    {
        var h = Head;
        if (h is null) return new(new VHead<T> { Count = 1, Tail = OneItemTail(item) });

        var c = h.Count;
        var r = new VHead<T> { Count = c + 1, Shift = h.Shift, Root = h.Root };
        if ((c & VTree<T>.Mask) != 0)
        {
            r.Tail = VTree<T>.Append(h.Tail, VTree<T>.TailSize(c), item);
        }
        else
        {
            (r.Root, r.Shift) = VTree<T>.PushLeaf(h.Root, h.Shift, c, h.Tail, null);   // the full tail joins the tree
            r.Tail = OneItemTail(item);
        }
        return new(r);
    }

    private static VTail<T> OneItemTail(T item)
    {
        var t = new VTail<T> { Fill = 1 };
        t.Items[0] = item;
        return t;
    }

    public ImmVector<T> AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return this;
        var t = Transient.Rent(Head);
        try { t.AddRange(items); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmVector<T> AddRange(IEnumerable<T> items)
    {
        var t = Transient.Rent(Head);
        try
        {
            foreach (var x in items) t.Add(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    public ImmVector<T> SetItem(int index, T item)
    {
        var h = Head;
        if (h is null || (uint)index >= (uint)h.Count) Throw.IndexOutOfRange();
        var tailOff = VTree<T>.TailOffset(h.Count);
        var r = new VHead<T> { Count = h.Count, Shift = h.Shift, Root = h.Root, Tail = h.Tail };
        if (index >= tailOff)
        {
            r.Tail = VTree<T>.NewTail(((ReadOnlySpan<T>)h.Tail.Items)[..(h.Count - tailOff)]);
            r.Tail.Items[index & VTree<T>.Mask] = item;
        }
        else r.Root = VTree<T>.Assoc(h.Shift, h.Root!, index, item, null);
        return new(r);
    }

    public ImmVector<T> Update(int index, Func<T, T> update) => SetItem(index, update(this[index]));

    public ImmVector<T> Update<TArg>(int index, TArg arg, Func<T, TArg, T> update)
        where TArg : allows ref struct
        => SetItem(index, update(this[index], arg));

    public ImmVector<T> RemoveLast()
    {
        var h = Head;
        if (h is null) Throw.Empty();
        var c = h.Count;
        if (c == 1) return default;

        var tailOff = VTree<T>.TailOffset(c);
        var r = new VHead<T> { Count = c - 1 };
        if (c - tailOff > 1)
        {
            r.Root = h.Root;
            r.Shift = h.Shift;
            r.Tail = VTree<T>.KeepTail(h.Tail, c - 1 - tailOff);
        }
        else
        {
            r.Tail = VTree<T>.LeafFor(h.Root!, h.Shift, c - 2);                 // the tree's last leaf, as it is
            (r.Root, r.Shift) = VTree<T>.Collapse(VTree<T>.PopTail(c, h.Shift, h.Root!, null), h.Shift);
        }
        return new(r);
    }

    /// <summary>
    /// This version, with a tail that holds none of other versions' elements. A tail is shared: the first append to
    /// a version writes into its tail in place, and pops and <see cref="Take"/> keep the tail when the elements hold
    /// no references. So a version can keep alive up to 31 elements appended by versions made from it; trim one kept
    /// long after them to let those go. Returns this version when its tail holds only its own (O(1)), else a new one
    /// with a copy of the tail; appending to the result shares its tail again.
    /// </summary>
    public ImmVector<T> TrimExcess()
    {
        var h = Head;
        if (h is null) return this;
        var used = VTree<T>.TailSize(h.Count);
        if (used == VTree<T>.Width || h.Tail is VTail<T> t && Volatile.Read(ref t.Fill) == used) return this;
        var tail = VTree<T>.NewTail(((ReadOnlySpan<T>)h.Tail.Items)[..used]);
        return new(new VHead<T> { Count = h.Count, Shift = h.Shift, Root = h.Root, Tail = tail });
    }

    /// <summary>First <paramref name="count"/> elements (immer::take). Shares structure with the source.</summary>
    public ImmVector<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var h = Head;
        if (h is null || count >= h.Count) return this;
        if (count == 0) return default;

        var tailOff = VTree<T>.TailOffset(h.Count);
        var r = new VHead<T> { Count = count };
        if (count > tailOff)
        {
            r.Root = h.Root;
            r.Shift = h.Shift;
            r.Tail = VTree<T>.KeepTail(h.Tail, count - tailOff);
        }
        else
        {
            var nto = VTree<T>.TailOffset(count);
            r.Tail = VTree<T>.KeepTail(VTree<T>.LeafFor(h.Root!, h.Shift, count - 1), count - nto);
            (r.Root, r.Shift) = VTree<T>.Trim(h.Root!, h.Shift, nto, null);
        }
        return new(r);
    }

    /// <summary>
    /// Applies a batch of in-place mutations and returns the resulting persistent value. The batch receives a
    /// <see cref="Scope"/> (a ref struct over a thread-local cached transient), so the compiler rejects any attempt
    /// to store or capture it, and the only allocations are the nodes actually written.
    /// </summary>
    public ImmVector<T> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(Head);
        try { batch(new Scope(t), arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmVector<T> Mutate(Action<Scope> batch)
    {
        var t = Transient.Rent(Head);
        try { batch(new Scope(t)); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public Transient ToTransient() => new(Head);

    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < Count) throw new ArgumentException("Destination too short.", nameof(destination));
        foreach (var chunk in Chunks)
        {
            chunk.CopyTo(destination);
            destination = destination[chunk.Length..];
        }
    }

    public T[] ToArray()
    {
        if (Head is null) return [];
        var a = GC.AllocateUninitializedArray<T>(Head.Count);
        CopyTo(a);
        return a;
    }

    public Enumerator GetEnumerator() => new(Head);
    internal Cursor GetCursor() => new(Head);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetCursor();
    IEnumerator IEnumerable.GetEnumerator() => GetCursor();

    bool IRefEnumerable<T, Enumerator>.TryGetSpan(out ReadOnlySpan<T> span)
    {
        foreach (var chunk in Chunks)
        {
            span = chunk.Length == Count ? chunk : default;
            return chunk.Length == Count;
        }
        span = default;
        return true; // empty
    }

    bool IRefEnumerable<T, Enumerator>.TryCopyTo(int index, Span<T> destination)
    {
        var n = RefEnumerable.CopyLength(Count, index, destination.Length);
        if (n <= RefEnumerable.RandomAccessMax)
        {
            for (var i = 0; i < n; i++) destination[i] = this[index + i];
            return true;
        }
        int skipped = 0, written = 0;
        foreach (var chunk in Chunks)
            if (RefEnumerable.CopyChunk(chunk, destination, index, n, ref skipped, ref written)) break;
        return true;
    }

    /// <summary>Iterates contiguous leaf spans (up to 32 elements each) — the fastest way to scan.</summary>
    public ChunkEnumerable Chunks => new(Head);

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<T>
    {
        private readonly VHead<T>? _h;
        private readonly int _count;
        private readonly int _tailOff;
        private VLeaf<T>? _leaf;
        private int _i;

        internal Cursor(VHead<T>? h)
        {
            _h = h;
            _count = h?.Count ?? 0;
            _tailOff = h is null ? 0 : VTree<T>.TailOffset(h.Count);
            _leaf = null;
            _i = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var i = _i + 1;
            if (i >= _count) { _i = _count; return false; }
            _i = i;
            if ((i & VTree<T>.Mask) == 0 && i < _tailOff) _leaf = VTree<T>.LeafFor(_h!.Root!, _h.Shift, i);
            return true;
        }

        public readonly ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _i < _tailOff ? ref _leaf!.Items[_i & VTree<T>.Mask] : ref _h!.Tail.Items[_i & VTree<T>.Mask];
        }

        T IEnumerator<T>.Current => Current;
        object? IEnumerator.Current => Current;
        public void Reset() { _i = -1; _leaf = null; }
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c>, so it can never be boxed or captured, which lets it
    /// hold the current leaf as a <see cref="ReadOnlySpan{T}"/>. Within a leaf, <see cref="MoveNext"/> is an index
    /// increment and a length compare; the tree is consulted only at leaf boundaries (one O(log n) leaf lookup per 32 elements). The
    /// <see cref="IEnumerable{T}"/> path hands out a separate heap-safe cursor instead (one allocation, as LINQ expects).
    /// </summary>
    public ref struct Enumerator : IEnumerator<T>
    {
        private readonly VHead<T>? _h;
        private ChunkEnumerator _chunks;
        private ReadOnlySpan<T> _span;
        private int _i;

        internal Enumerator(VHead<T>? h)
        {
            _h = h;
            _chunks = new(h);
            _span = default;
            _i = -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var i = _i + 1;
            if (i < _span.Length)
            {
                _i = i;
                return true;
            }
            return NextChunk();
        }

        private bool NextChunk()
        {
            while (_chunks.MoveNext())
            {
                _span = _chunks.Current;
                if (_span.Length != 0)
                {
                    _i = 0;
                    return true;
                }
            }
            _span = default;
            _i = -1;
            return false;
        }

        public readonly ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _span[_i];
        }

        T IEnumerator<T>.Current => Current;

        readonly object? IEnumerator.Current => Current;
        public void Reset() => this = new(_h);
        public readonly void Dispose() { }
    }

    public readonly struct ChunkEnumerable
    {
        private readonly VHead<T>? _head;
        internal ChunkEnumerable(VHead<T>? head) => _head = head;
        public ChunkEnumerator GetEnumerator() => new(_head);
    }

    public struct ChunkEnumerator
    {
        private readonly VHead<T>? _h;
        private readonly int _count;
        private readonly int _tailOff;
        private VLeaf<T>? _leaf;
        private int _start;

        internal ChunkEnumerator(VHead<T>? h)
        {
            _h = h;
            _count = h?.Count ?? 0;
            _tailOff = h is null ? 0 : VTree<T>.TailOffset(h.Count);
            _leaf = null;
            _start = -VTree<T>.Width;
        }

        public bool MoveNext()
        {
            var s = _start + VTree<T>.Width;
            if (s >= _count) return false;
            _start = s;
            if (s < _tailOff) _leaf = VTree<T>.LeafFor(_h!.Root!, _h.Shift, s);
            return true;
        }

        public readonly ReadOnlySpan<T> Current =>
            _start < _tailOff ? _leaf!.Items : ((ReadOnlySpan<T>)_h!.Tail.Items)[..(_count - _start)];
    }

    /// <summary>
    /// Non-escapable mutation handle passed to <see cref="Mutate{TArg}"/>. Being a <c>ref struct</c> it cannot be
    /// stored in a field, captured by a lambda, boxed, or used across <c>await</c>, so the pooled transient behind it
    /// can never be touched after the batch completes. Zero-cost: a single reference, all members forward.
    /// </summary>
    public readonly ref struct Scope
    {
        private readonly Transient _t;
        internal Scope(Transient t) => _t = t;

        public int Count => _t.Count;
        public T this[int index] { get => _t[index]; set => _t[index] = value; }
        /// <inheritdoc cref="Transient.ItemRef"/>
        public ref T ItemRef(int index) => ref _t.ItemRef(index);
        public void Add(T item) => _t.Add(item);
        public void AddRange(ReadOnlySpan<T> items) => _t.AddRange(items);
        public void RemoveLast() => _t.RemoveLast();
        public void Take(int count) => _t.Take(count);
        public void Clear() => _t.Clear();
    }

    /// <summary>
    /// Mutable view (immer::vector_transient). Nodes created by this transient are mutated in place; shared
    /// nodes are copied on first write. Not thread-safe; values produced by <see cref="ToPersistent"/> are.
    /// A transient can be reused indefinitely (<see cref="Reset"/>) to avoid allocating new transients.
    /// </summary>
    public sealed class Transient
    {
        private int _count;
        private int _shift = VTree<T>.Bits;
        private VInner<T>? _root;
        private Buffer32<T> _tail;
        private Owner? _owner;
        private VHead<T>? _snapshot; // last persistent value, valid while unmodified

        [ThreadStatic] private static Transient? t_cache;

        public Transient() { }

        internal Transient(VHead<T>? head) => Reset(new(head));

        internal static Transient Rent(VHead<T>? head)
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

        private Owner Token => _owner ??= new Owner();

        public int Count => _count;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) Throw.IndexOutOfRange();
                if (index >= VTree<T>.TailOffset(_count)) return _tail[index & VTree<T>.Mask];
                return VTree<T>.LeafFor(_root!, _shift, index).Items[index & VTree<T>.Mask];
            }
            set => ItemRef(index) = value;
        }

        /// <summary>
        /// Mutable reference to an element (zero-alloc once the path is owned). Invalidated by any structural
        /// change or by <see cref="ToPersistent"/>; never write through it afterwards.
        /// </summary>
        public ref T ItemRef(int index)
        {
            if ((uint)index >= (uint)_count) Throw.IndexOutOfRange();
            _snapshot = null;
            if (index >= VTree<T>.TailOffset(_count)) return ref _tail[index & VTree<T>.Mask];
            return ref VTree<T>.EditRef(ref _root!, _shift, index, Token);
        }

        public void Add(T item)
        {
            var c = _count;
            _snapshot = null;
            if (c != 0 && (c & VTree<T>.Mask) == 0) PushTailLeaf(c);
            _tail[c & VTree<T>.Mask] = item;
            _count = c + 1;
        }

        public void AddRange(ReadOnlySpan<T> items)
        {
            if (items.IsEmpty) return;
            _snapshot = null;
            var c = _count;
            var used = VTree<T>.TailSize(c);
            if (used < VTree<T>.Width)
            {
                var n = Math.Min(VTree<T>.Width - used, items.Length);
                items[..n].CopyTo(((Span<T>)_tail)[used..]);
                c += n;
                items = items[n..];
                if (items.IsEmpty) { _count = c; return; }
            }

            // Tail is full and more items follow: flush it, then build whole leaves straight from the span.
            var o = Token;
            var leaf = new VPlainLeaf<T> { Owner = o };
            leaf.Items = _tail;
            (_root, _shift) = VTree<T>.PushLeaf(_root, _shift, c, leaf, o);
            while (items.Length > VTree<T>.Width)
            {
                leaf = new VPlainLeaf<T> { Owner = o };
                items[..VTree<T>.Width].CopyTo(leaf.Items);
                c += VTree<T>.Width;
                (_root, _shift) = VTree<T>.PushLeaf(_root, _shift, c, leaf, o);
                items = items[VTree<T>.Width..];
            }
            VTree<T>.CopyTail(items, _tail, items.Length);
            _count = c + items.Length;
        }

        private void PushTailLeaf(int c)
        {
            var o = Token;
            var leaf = new VPlainLeaf<T> { Owner = o };
            leaf.Items = _tail;
            (_root, _shift) = VTree<T>.PushLeaf(_root, _shift, c, leaf, o);
            VTree<T>.ClearFrom(_tail, 0);
        }

        public void RemoveLast()
        {
            var c = _count;
            if (c == 0) Throw.Empty();
            _snapshot = null;
            var tailOff = VTree<T>.TailOffset(c);
            if (c - tailOff > 1 || c == 1)
            {
                VTree<T>.ClearFrom(((Span<T>)_tail)[..(c - tailOff)], c - 1 - tailOff);
                _count = c - 1;
                return;
            }
            _tail = VTree<T>.LeafFor(_root!, _shift, c - 2).Items;
            (_root, _shift) = VTree<T>.Collapse(VTree<T>.PopTail(c, _shift, _root!, Token), _shift);
            _count = c - 1;
        }

        /// <summary>Truncates to the first <paramref name="count"/> elements.</summary>
        public void Take(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            var c = _count;
            if (count >= c) return;
            _snapshot = null;
            if (count == 0) { Clear(); return; }
            var tailOff = VTree<T>.TailOffset(c);
            if (count > tailOff)
            {
                VTree<T>.ClearFrom(_tail, count - tailOff);
            }
            else
            {
                var nto = VTree<T>.TailOffset(count);
                VTree<T>.CopyTail(VTree<T>.LeafFor(_root!, _shift, count - 1).Items, _tail, count - nto);
                (_root, _shift) = VTree<T>.Trim(_root!, _shift, nto, Token);
            }
            _count = count;
        }

        public void Clear()
        {
            _count = 0;
            _shift = VTree<T>.Bits;
            _root = null;
            _owner = null;
            _snapshot = null;
            VTree<T>.ClearFrom(_tail, 0);
        }

        /// <summary>Re-targets this transient at <paramref name="source"/> (O(1) + tail copy), reusing the instance.</summary>
        public void Reset(ImmVector<T> source)
        {
            var h = source.Head;
            if (h is null) { Clear(); return; }
            _count = h.Count;
            _shift = h.Shift;
            _root = h.Root;
            _owner = null;
            VTree<T>.CopyTail(h.Tail.Items, _tail, VTree<T>.TailSize(h.Count));
            _snapshot = h;
        }

        /// <summary>
        /// Freezes the current contents into a persistent value (O(1) + tail copy). The transient stays usable:
        /// subsequent writes copy-on-write, so the returned value is never affected.
        /// </summary>
        public ImmVector<T> ToPersistent()
        {
            if (_snapshot is not null || _count == 0) return new(_snapshot);
            var tail = VTree<T>.NewTail(((ReadOnlySpan<T>)_tail)[..VTree<T>.TailSize(_count)]);
            var h = new VHead<T> { Count = _count, Shift = _shift, Root = _root, Tail = tail };
            _owner = null; // freeze every node created so far
            _snapshot = h;
            return new(h);
        }

        public Enumerator GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

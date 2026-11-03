using System.Collections;
using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

public static class ImmFlexVector
{
    public static ImmFlexVector<T> Create<T>(ReadOnlySpan<T> items) => ImmFlexVector<T>.Empty.AddRange(items);

    public static ImmFlexVector<T> CreateRange<T>(IEnumerable<T> items) => ImmFlexVector<T>.Empty.AddRange(items);

    public static ImmFlexVector<T>.Transient CreateTransient<T>() => new();
}

/// <summary>
/// Persistent RRB vector (immer::flex_vector): O(log n) concat, split (Take/Skip/Slice), Insert and RemoveAt, in
/// addition to <see cref="ImmVector{T}"/>'s indexed access and amortised O(1) append. Indexing is marginally slower
/// than <see cref="ImmVector{T}"/> once nodes become relaxed (size-table lookup). Single-reference readonly struct.
/// </summary>
[CollectionBuilder(typeof(ImmFlexVector), nameof(ImmFlexVector.Create))]
public readonly struct ImmFlexVector<T> : IReadOnlyList<T>, IRefEnumerable<T, ImmFlexVector<T>.Enumerator>, IImmutable
{
    internal readonly FHead<T>? Head;

    internal ImmFlexVector(FHead<T>? head) => Head = head is null || head.Count == 0 ? null : head;

    public static ImmFlexVector<T> Empty => default;

    public int Count => Head is null ? 0 : Head.Count;

    public bool IsEmpty => Head is null;

    public bool IsSameAs(ImmFlexVector<T> other) => ReferenceEquals(Head, other.Head);

    public ref readonly T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var h = Head;
            if (h is null || (uint)index >= (uint)h.Count) Throw.IndexOutOfRange();
            return ref FTree<T>.ItemRef(h, index);
        }
    }

    T IReadOnlyList<T>.this[int index] => this[index];

    public ref readonly T First
    {
        get
        {
            if (Head is null) Throw.Empty();    // as ImmVector and ImmOrderedMap: not an index error
            return ref this[0];
        }
    }

    public ref readonly T Last
    {
        get
        {
            if (Head is null) Throw.Empty();
            return ref this[Count - 1];
        }
    }

    public ImmFlexVector<T> Add(T item) => new(FTree<T>.Add(Head, item, null));

    public ImmFlexVector<T> AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return this;
        if (items.Length <= FTree<T>.Width) return new(FTree<T>.AddRange(Head, items, null)); // at most one leaf flush
        var t = Transient.Rent(Head); // owned nodes: each flush mutates the right edge in place
        try { t.AddRange(items); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmFlexVector<T> AddRange(IEnumerable<T> items)
    {
        var t = Transient.Rent(Head);
        try
        {
            foreach (var x in items) t.Add(x);
            return t.ToPersistent();
        }
        finally { Transient.Return(t); }
    }

    /// <summary>O(log n) concatenation (structure of both inputs is shared).</summary>
    public ImmFlexVector<T> AddRange(ImmFlexVector<T> other) => new(FTree<T>.Concat(Head, other.Head, null));

    public static ImmFlexVector<T> operator +(ImmFlexVector<T> left, ImmFlexVector<T> right) => left.AddRange(right);

    public ImmFlexVector<T> Prepend(T item) => Insert(0, item);

    public ImmFlexVector<T> Insert(int index, T item)
    {
        if ((uint)index > (uint)Count) Throw.IndexOutOfRange();
        return new(FTree<T>.Insert(Head, index, item, null));
    }

    public ImmFlexVector<T> InsertRange(int index, ImmFlexVector<T> items)
    {
        if ((uint)index > (uint)Count) Throw.IndexOutOfRange();
        if (items.IsEmpty) return this;
        return Take(index).AddRange(items).AddRange(Skip(index));
    }

    public ImmFlexVector<T> RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count) Throw.IndexOutOfRange();
        return new(FTree<T>.RemoveRange(Head!, index, 1, null));
    }

    public ImmFlexVector<T> RemoveRange(int index, int count)
    {
        if ((uint)index > (uint)Count || (uint)count > (uint)(Count - index)) Throw.IndexOutOfRange();
        return Head is null ? this : new(FTree<T>.RemoveRange(Head, index, count, null));
    }

    /// <summary>Without the last element: a header copy, as an append is (the tree's last leaf moves into the tail when
    /// the tail runs out).</summary>
    public ImmFlexVector<T> RemoveLast()
    {
        if (IsEmpty) Throw.Empty();
        return new(FTree<T>.RemoveLast(Head!, null));
    }

    /// <summary>
    /// This version, with a tail that holds none of other versions' elements. A tail is shared: the first append to a
    /// version writes into its tail in place, and pops and <see cref="Take"/> keep the tail when the elements hold no
    /// references. So a version can keep alive up to 31 elements appended by versions made from it; trim one kept long
    /// after them to let those go. Returns this version when its tail holds only its own (O(1)), else a new one with a
    /// copy of the tail; appending to the result shares its tail again.
    /// </summary>
    public ImmFlexVector<T> TrimExcess() => Head is null ? this : new(FTree<T>.TrimExcess(Head));

    public ImmFlexVector<T> SetItem(int index, T item)
    {
        var h = Head;
        if (h is null || (uint)index >= (uint)h.Count) Throw.IndexOutOfRange();
        FTree<T>.EditItem(ref h, index, null) = item;
        return new(h);
    }

    public ImmFlexVector<T> Update(int index, Func<T, T> update) => SetItem(index, update(this[index]));

    public ImmFlexVector<T> Update<TArg>(int index, TArg arg, Func<T, TArg, T> update) where TArg : allows ref struct
        => SetItem(index, update(this[index], arg));

    /// <summary>First <paramref name="count"/> elements, O(log n).</summary>
    public ImmFlexVector<T> Take(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Head is null ? this : new(FTree<T>.Take(Head, count, null));
    }

    /// <summary>All but the first <paramref name="count"/> elements (immer::drop), O(log n).</summary>
    public ImmFlexVector<T> Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Head is null ? this : new(FTree<T>.Drop(Head, count, null));
    }

    public ImmFlexVector<T> Slice(int start, int length)
    {
        if ((uint)start > (uint)Count || (uint)length > (uint)(Count - start)) Throw.IndexOutOfRange();
        return Skip(start).Take(length);
    }

    public ImmFlexVector<T> this[Range range]
    {
        get
        {
            var (start, length) = range.GetOffsetAndLength(Count);
            return Slice(start, length);
        }
    }

    public ImmFlexVector<T> Mutate<TArg>(TArg arg, Action<Scope, TArg> batch) where TArg : allows ref struct
    {
        var t = Transient.Rent(Head);
        try { batch(new Scope(t), arg); return t.ToPersistent(); }
        finally { Transient.Return(t); }
    }

    public ImmFlexVector<T> Mutate(Action<Scope> batch)
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

    /// <summary>Contiguous leaf spans (1..32 elements each), then the tail.</summary>
    public ChunkEnumerable Chunks => new(Head);

    /// <summary>Heap-safe state machine behind <see cref="Enumerator"/> and the <c>IEnumerable</c> path.</summary>
    internal struct Cursor : IEnumerator<T>
    {
        private readonly FHead<T>? _h;
        private FLeafCursor<T> _cursor;
        private FLeaf<T>? _leaf;
        private int _i;
        private int _len;
        private bool _inTail;

        internal Cursor(FHead<T>? h)
        {
            _h = h;
            _cursor = new(h?.Root);
            _leaf = null;
            _i = _len = 0;
            _inTail = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            if (++_i < _len) return true;
            return NextChunk();
        }

        private bool NextChunk()
        {
            if (_h is null || _inTail) return false;
            var leaf = _cursor.Next();
            if (leaf is not null)
            {
                _leaf = leaf;
                _len = leaf.Count;
                _i = 0;
                return true;
            }
            _inTail = true;
            _leaf = null;
            _len = _h.TailLen;
            _i = 0;
            return _len != 0;
        }

        public readonly ref readonly T Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _inTail ? ref _h!.Tail!.Items[_i] : ref _leaf!.Items[_i];
        }

        T IEnumerator<T>.Current => Current;
        object? IEnumerator.Current => Current;
        public void Reset() => this = new(_h);
        public readonly void Dispose() { }
    }

    /// <summary>
    /// Duck-typed <c>foreach</c> enumerator: a <c>ref struct</c>, so it can never be boxed or captured, which lets it
    /// hold the current leaf as a <see cref="ReadOnlySpan{T}"/>. Within a leaf, <see cref="MoveNext"/> is an index
    /// increment and a length compare. The <see cref="IEnumerable{T}"/> path hands out a separate heap-safe cursor.
    /// <para>Kept deliberately small (no inline cursor stack) so the JIT can keep the hot fields in registers: the
    /// position in the tree is the current leaf's parent plus a path of child indices packed one byte per level.
    /// Moving to a sibling leaf is O(1); only when a parent is exhausted are its ancestors re-walked from the root
    /// (once per ~32 leaves).</para>
    /// </summary>
    public ref struct Enumerator : IEnumerator<T>
    {
        private const int Done = 3, InTail = 2, InTree = 1;

        private readonly FHead<T>? _h;
        private ReadOnlySpan<T> _span;
        private int _i;
        private FBranch<T>? _parent;   // branch holding the current leaf
        private ulong _path;           // child index per level (root level in the low byte); depth <= 8 as for FLeafCursor
        private int _depth;            // levels on the path: the current leaf is child Index(_depth - 1) of _parent
        private int _state;            // 0 = not started, InTree, InTail, Done

        internal Enumerator(FHead<T>? h)
        {
            _h = h;
            _span = default;
            _i = -1;
            _parent = null;
            _path = 0;
            _depth = 0;
            _state = 0;
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

        // Inlined into the caller's loop. Sibling steps happen inline; everything else is a static call taking the
        // cold state by value. Passing `ref this` to an out-of-line method would expose the enumerator's address,
        // and the JIT would then keep _span / _i on the stack instead of in registers.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool NextChunk()
        {
            if (_state == InTree)
            {
                // Common case, kept in the caller's registers: the next sibling leaf under the same parent.
                int last = _depth - 1, k = Index(_path, last) + 1;
                var parent = _parent!;
                if (k < parent.Len && FTree<T>.LeafOrNull(parent.C[k]) is { Count: > 0 } leaf)
                {
                    _path += 1UL << (last << 3);   // index at `last` is k - 1 < 255: increment in place
                    _span = ((ReadOnlySpan<T>)leaf.Items)[..leaf.Count];
                    _i = 0;
                    return true;
                }
            }
            else if (_state == Done) return false;
            var p = Advance(_h, _parent, _path, _depth, _state);
            _parent = p.Parent;
            _path = p.Path;
            _depth = p.Depth;
            _state = p.State;
            _span = p.Span;
            _i = p.Span.IsEmpty ? -1 : 0;
            return !p.Span.IsEmpty;
        }

        private ref struct Position
        {
            public ReadOnlySpan<T> Span;
            public FBranch<T>? Parent;
            public ulong Path;
            public int Depth;
            public int State;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Position Advance(FHead<T>? h, FBranch<T>? parent, ulong path, int depth, int state)
        {
            var p = new Position { Parent = parent, Path = path, Depth = depth, State = state };
            while (p.State != Done)
            {
                if (p.State != InTail)
                {
                    var leaf = p.State == 0 ? (h?.Root is { } root ? Leftmost(root, 0, ref p) : null) : NextLeaf(h!, ref p);
                    if (leaf is not null)
                    {
                        p.State = InTree;
                        if (leaf.Count == 0) continue;
                        p.Span = ((ReadOnlySpan<T>)leaf.Items)[..leaf.Count];
                        return p;
                    }
                    p.State = InTail;
                    if (h is not null && h.TailLen != 0)
                    {
                        p.Span = ((ReadOnlySpan<T>)h.Tail!.Items)[..h.TailLen];
                        return p;
                    }
                }
                p.State = Done;
            }
            return p;
        }

        private static int Index(ulong path, int level) => (int)(path >> (level << 3)) & 0xFF;

        private static void SetIndex(ref Position p, int level, int k) =>
            p.Path = (p.Path & ~(0xFFUL << (level << 3))) | ((ulong)k << (level << 3));

        /// <summary>First leaf under <paramref name="node"/> (at <paramref name="level"/>), recording the path.</summary>
        private static FLeaf<T>? Leftmost(FBranch<T> node, int level, ref Position p)
        {
            while (true)
            {
                if (node.Len == 0) return null;
                SetIndex(ref p, level, 0);
                var c = node.C[0]!;
                if (FTree<T>.LeafOrNull(c) is { } leaf)
                {
                    p.Parent = node;
                    p.Depth = level + 1;
                    return leaf;
                }
                node = FTree<T>.AsBranch(c);
                level++;
            }
        }

        private static FLeaf<T>? NextLeaf(FHead<T> h, ref Position p)
        {
            int last = p.Depth - 1, k = Index(p.Path, last) + 1;
            var parent = p.Parent!;
            if (k < parent.Len)
            {
                SetIndex(ref p, last, k);
                var c = parent.C[k]!;
                return FTree<T>.LeafOrNull(c) ?? Leftmost(FTree<T>.AsBranch(c), p.Depth, ref p);
            }
            // The parent is exhausted: re-walk its ancestors from the root and step to the next subtree.
            Buffer8<FBranch<T>?> nodes = default;
            nodes[0] = h.Root;
            for (var l = 0; l + 1 < p.Depth; l++) nodes[l + 1] = FTree<T>.AsBranch(nodes[l]!.C[Index(p.Path, l)]!);
            for (var l = p.Depth - 2; l >= 0; l--)
            {
                var n = nodes[l]!;
                var next = Index(p.Path, l) + 1;
                if (next >= n.Len) continue;
                SetIndex(ref p, l, next);
                var c = n.C[next]!;
                if (FTree<T>.LeafOrNull(c) is { } leaf)
                {
                    p.Parent = n;
                    p.Depth = l + 1;
                    return leaf;
                }
                return Leftmost(FTree<T>.AsBranch(c), l + 1, ref p);
            }
            return null;
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
        private readonly FHead<T>? _head;
        internal ChunkEnumerable(FHead<T>? head) => _head = head;
        public ChunkEnumerator GetEnumerator() => new(_head);
    }

    public struct ChunkEnumerator
    {
        private readonly FHead<T>? _h;
        private FLeafCursor<T> _cursor;
        private FLeaf<T>? _leaf;
        private bool _inTail;

        internal ChunkEnumerator(FHead<T>? h)
        {
            _h = h;
            _cursor = new(h?.Root);
            _leaf = null;
            _inTail = false;
        }

        public bool MoveNext()
        {
            if (_h is null || _inTail) return false;
            _leaf = _cursor.Next();
            if (_leaf is not null) return true;
            _inTail = true;
            return _h.TailLen != 0;
        }

        public readonly ReadOnlySpan<T> Current =>
            _inTail ? ((ReadOnlySpan<T>)_h!.Tail!.Items)[.._h.TailLen] : ((ReadOnlySpan<T>)_leaf!.Items)[.._leaf.Count];
    }

    /// <summary>Non-escapable mutation handle passed to <see cref="Mutate{TArg}"/> (see <see cref="ImmVector{T}.Scope"/>).</summary>
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
        public void AddRange(ImmFlexVector<T> items) => _t.AddRange(items);
        public void Insert(int index, T item) => _t.Insert(index, item);
        public void RemoveAt(int index) => _t.RemoveAt(index);
        public void RemoveRange(int index, int count) => _t.RemoveRange(index, count);
        public void Take(int count) => _t.Take(count);
        public void Skip(int count) => _t.Skip(count);
        public void Clear() => _t.Clear();
    }

    /// <summary>
    /// Mutable view (immer::flex_vector_transient). Its header and every node it creates are owned and updated in
    /// place; appends into the tail and writes to owned leaves are allocation-free. Not thread-safe.
    /// </summary>
    public sealed class Transient
    {
        private FHead<T>? _h;
        private Owner? _owner;

        [ThreadStatic] private static Transient? t_cache;

        public Transient() { }

        internal Transient(FHead<T>? head) => _h = head;

        internal static Transient Rent(FHead<T>? head)
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

        public int Count => _h?.Count ?? 0;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) Throw.IndexOutOfRange();
                return FTree<T>.ItemRef(_h!, index);
            }
            set => ItemRef(index) = value;
        }

        /// <summary>Mutable element reference; invalidated by any structural change or <see cref="ToPersistent"/>.</summary>
        public ref T ItemRef(int index)
        {
            if ((uint)index >= (uint)Count) Throw.IndexOutOfRange();
            return ref FTree<T>.EditItem(ref _h!, index, Token);
        }

        public void Add(T item) => _h = FTree<T>.Add(_h, item, Token);

        public void AddRange(ReadOnlySpan<T> items)
        {
            if (!items.IsEmpty) _h = FTree<T>.AddRange(_h, items, Token);
        }

        public void AddRange(ImmFlexVector<T> items) => _h = FTree<T>.Concat(_h, items.Head, Token);

        public void Insert(int index, T item)
        {
            if ((uint)index > (uint)Count) Throw.IndexOutOfRange();
            _h = FTree<T>.Insert(_h, index, item, Token);
        }

        public void RemoveAt(int index) => RemoveRange(index, 1);

        public void RemoveRange(int index, int count)
        {
            if ((uint)index > (uint)Count || (uint)count > (uint)(Count - index)) Throw.IndexOutOfRange();
            if (_h is not null) _h = FTree<T>.RemoveRange(_h, index, count, Token);
        }

        public void RemoveLast()
        {
            if (Count == 0) Throw.Empty();
            _h = FTree<T>.RemoveLast(_h!, Token);
        }

        public void Take(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_h is not null) _h = FTree<T>.Take(_h, count, Token);
        }

        public void Skip(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_h is not null) _h = FTree<T>.Drop(_h, count, Token);
        }

        public void Clear()
        {
            _h = null;
            _owner = null;
        }

        public void Reset(ImmFlexVector<T> source)
        {
            _h = source.Head;
            _owner = null;
        }

        /// <summary>O(1) freeze; the transient remains usable (later writes copy-on-write).</summary>
        public ImmFlexVector<T> ToPersistent()
        {
            _owner = null;
            return new(_h);
        }

        public Enumerator GetEnumerator() => ToPersistent().GetEnumerator();
    }
}

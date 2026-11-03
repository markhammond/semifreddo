using System.Numerics;
using System.Runtime.CompilerServices;

namespace Semifreddo.Internal;

// CHAMP (Compressed Hash-Array Mapped Prefix-tree; Steindorfer & Vinju 2015) as used by immer::map/set.
// Entries are stored inline in a struct array; sub-nodes in a separate array; canonical form is maintained on
// removal so equal sets always have equal shapes. Hasher and entry types are static-abstract generic
// parameters so the JIT/AOT compiler specialises and inlines hashing, equality and key projection.

internal interface IHasher<K>
{
    static abstract int Hash(K key);
    static abstract bool Eq(K a, K b);
}

internal readonly struct DefaultHasher<K> : IHasher<K>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(K key) => key is null ? 0 : EqualityComparer<K>.Default.GetHashCode(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Eq(K a, K b) => EqualityComparer<K>.Default.Equals(a, b);
}

internal static class ChampHash
{
    /// <summary>murmur3 fmix32: bijective, so it never adds collisions, but spreads poorly-distributed hashes
    /// (sequential ids, tick-aligned prices) across the low bits that index the first levels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mix(uint h)
    {
        h ^= h >> 16; h *= 0x85EBCA6B;
        h ^= h >> 13; h *= 0xC2B2AE35;
        h ^= h >> 16;
        return h;
    }
}

/// <summary>
/// What a lookup needs from its key: the (mixed) hash, equality against stored keys, and how to materialise a
/// stored key on insert. Lets one trie walk serve both <c>K</c> and alternate (e.g. span) keys; the JIT
/// specialises per probe type, so the K path compiles to the same code as a direct implementation.
/// </summary>
internal interface IKeyProbe<K>
{
    uint Hash { get; }
    bool Matches(K stored);
    K ToKey();
}

internal readonly struct KeyProbe<K, H>(K key, uint hash) : IKeyProbe<K> where H : struct, IHasher<K>
{
    public uint Hash => hash;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(K stored) => H.Eq(stored, key);

    public K ToKey() => key;
}

/// <summary>Probe for an alternate key (cf. <c>Dictionary.GetAlternateLookup</c>); may itself wrap a ref struct.</summary>
internal readonly ref struct AltProbe<TAlt, K>(TAlt alternate, IAlternateEqualityComparer<TAlt, K> comparer) : IKeyProbe<K>
    where TAlt : allows ref struct
{
    private readonly TAlt _alt = alternate;
    public uint Hash { get; } = ChampHash.Mix((uint)comparer.GetHashCode(alternate));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(K stored) => comparer.Equals(_alt, stored);

    public K ToKey() => comparer.Create(_alt);
}

internal interface IChampEntry<TSelf, K> where TSelf : struct, IChampEntry<TSelf, K>
{
    static abstract K KeyOf(in TSelf e);
    static abstract TSelf FromKey(K key);

    /// <summary>What a key's entry becomes when <paramref name="incoming"/> is set over <paramref name="existing"/>: the
    /// stored key is kept (as <c>Dictionary</c> keeps it), so persistent and transient writes agree.</summary>
    static abstract TSelf Replaced(in TSelf existing, in TSelf incoming);
}

internal struct MapEntry<K, V>(K key, V value) : IChampEntry<MapEntry<K, V>, K>
{
    public K Key = key;
    public V Value = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static K KeyOf(in MapEntry<K, V> e) => e.Key;

    public static MapEntry<K, V> FromKey(K key) => new(key, default!);

    public static MapEntry<K, V> Replaced(in MapEntry<K, V> existing, in MapEntry<K, V> incoming) => new(existing.Key, incoming.Value);
}

internal struct SetEntry<K>(K key) : IChampEntry<SetEntry<K>, K>
{
    public K Key = key;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static K KeyOf(in SetEntry<K> e) => e.Key;

    public static SetEntry<K> FromKey(K key) => new(key);

    public static SetEntry<K> Replaced(in SetEntry<K> existing, in SetEntry<K> incoming) => existing;
}

internal sealed class ChampNode<TE>(uint dataMap, uint nodeMap, TE[] data, ChampNode<TE>[] nodes, Owner? owner)
{
    public uint DataMap = dataMap;
    public uint NodeMap = nodeMap;
    public TE[] Data = data;               // exact-size; Length == PopCount(DataMap) (or #collisions)
    public ChampNode<TE>[] Nodes = nodes;  // exact-size; Length == PopCount(NodeMap)
    public Owner? Owner = owner;
    public int Count;                      // total entries; maintained on root nodes only
    public bool Slack;                     // owned node whose arrays may be over-allocated; trimmed at freeze
}

/// <summary>
/// Edit token for CHAMP transients. Owned bitmap nodes grow their arrays geometrically (spare capacity) instead of
/// reallocating on every insert; each such node is recorded here once and trimmed to exact size at freeze, so
/// persistent (frozen) nodes always satisfy <c>Data.Length == PopCount(DataMap)</c>.
/// </summary>
internal sealed class ChampOwner<TE>(List<ChampNode<TE>> slack) : Owner
{
    public readonly List<ChampNode<TE>> Slack = slack;
}

internal static class Champ<TE, K, H>
    where TE : struct, IChampEntry<TE, K>
    where H : struct, IHasher<K>
{
    private const int Bits = 5;
    private const int CollisionShift = 35; // 7 levels consume 32 hash bits (shift 0..30)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint HashOf(K key) => ChampHash.Mix((uint)H.Hash(key));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bit(uint hash, int shift) => 1u << (int)((hash >> shift) & 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Index(uint map, uint bit) => BitOperations.PopCount(map & (bit - 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Owned(ChampNode<TE> n, Owner? o) => o is not null && ReferenceEquals(n.Owner, o);

    // Owned (transient) nodes: arrays may have spare capacity, so logical sizes come from the bitmaps.
    // Collision nodes (shift >= 35) are rare and always kept exact-size.

    private static void MarkSlack(ChampNode<TE> n, Owner o)
    {
        if (n.Slack) return;
        n.Slack = true;
        ((ChampOwner<TE>)o).Slack.Add(n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Grow(int count) => count < 4 ? 4 : Math.Min(count * 2, 32);

    private static void InsertOwned<TA>(ref TA[] arr, int count, int i, in TA x)
    {
        if (count == arr.Length)
        {
            var na = new TA[Grow(count)];
            arr.AsSpan(0, i).CopyTo(na);
            arr.AsSpan(i, count - i).CopyTo(na.AsSpan(i + 1));
            arr = na;
        }
        else
        {
            arr.AsSpan(i, count - i).CopyTo(arr.AsSpan(i + 1));
        }
        arr[i] = x;
    }

    private static void RemoveOwned<TA>(TA[] arr, int count, int i)
    {
        arr.AsSpan(i + 1, count - i - 1).CopyTo(arr.AsSpan(i));
        arr[count - 1] = default!;
    }

    /// <summary>Trims every over-allocated node recorded during the batch to exact size (called at freeze).</summary>
    public static void Trim(List<ChampNode<TE>> slack)
    {
        foreach (var n in System.Runtime.InteropServices.CollectionsMarshal.AsSpan(slack))
        {
            int dc = BitOperations.PopCount(n.DataMap), nc = BitOperations.PopCount(n.NodeMap);
            if (n.Data.Length != dc) n.Data = dc == 0 ? [] : n.Data.AsSpan(0, dc).ToArray();
            if (n.Nodes.Length != nc) n.Nodes = nc == 0 ? [] : n.Nodes.AsSpan(0, nc).ToArray();
            n.Slack = false;
        }
        slack.Clear();
    }

    private static TA[] Copy<TA>(TA[] a) => a.Length == 0 ? a : a.AsSpan().ToArray();

    /// <summary>Persistent nodes may share untouched arrays; owned nodes must own theirs exclusively.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TA[] Own<TA>(TA[] a, Owner? o) => o is null ? a : Copy(a);

    private static TA[] InsertAt<TA>(TA[] a, int i, in TA x)
    {
        var r = new TA[a.Length + 1];
        a.AsSpan(0, i).CopyTo(r);
        r[i] = x;
        a.AsSpan(i).CopyTo(r.AsSpan(i + 1));
        return r;
    }

    private static TA[] RemoveAt<TA>(TA[] a, int i)
    {
        if (a.Length == 1) return [];
        var r = new TA[a.Length - 1];
        a.AsSpan(0, i).CopyTo(r);
        a.AsSpan(i + 1).CopyTo(r.AsSpan(i));
        return r;
    }

    public static ChampNode<TE> Singleton(in TE e, uint hash, Owner? o) => new(Bit(hash, 0), 0, [e], [], o);

    // ---------------------------------------------------------------------------------------------- lookup

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TE Find(ChampNode<TE>? root, K key)
    {
        if (root is null) return ref Unsafe.NullRef<TE>();
        return ref FindAt(root, key, HashOf(key), 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref TE FindAt(ChampNode<TE> n, K key, uint hash, int shift)
        => ref FindAt(n, new KeyProbe<K, H>(key, hash), shift);

    public static ref TE FindAt<TP>(ChampNode<TE> n, scoped TP probe, int shift) where TP : IKeyProbe<K>, allows ref struct
        => ref WithProbe<TP>.FindAt(n, probe, shift);

    /// <summary>Probe-generic form: the stored key is only materialised (<c>probe.ToKey()</c>) on insert.</summary>
    public static ref TE GetOrAdd<TP>(ref ChampNode<TE> slot, scoped TP probe, Owner o, out bool exists) where TP : IKeyProbe<K>, allows ref struct
        => ref WithProbe<TP>.GetOrAdd(ref slot, probe, o, out exists);

    public static ChampNode<TE> Remove<TP>(ChampNode<TE> n, scoped TP probe, int shift, Owner? o, ref bool removed) where TP : IKeyProbe<K>, allows ref struct
        => WithProbe<TP>.Remove(n, probe, shift, o, ref removed);

    // ---------------------------------------------------------------------------------------- node edits

    private static ChampNode<TE> ReplaceData(ChampNode<TE> n, int idx, in TE e, Owner? o)
    {
        var m = Owned(n, o) ? n : new ChampNode<TE>(n.DataMap, n.NodeMap, Copy(n.Data), Own(n.Nodes, o), o);
        m.Data[idx] = e;
        return m;
    }

    private static ChampNode<TE> ReplaceNode(ChampNode<TE> n, int idx, ChampNode<TE> child, Owner? o)
    {
        var m = Owned(n, o) ? n : new ChampNode<TE>(n.DataMap, n.NodeMap, Own(n.Data, o), Copy(n.Nodes), o);
        m.Nodes[idx] = child;
        return m;
    }

    private static ChampNode<TE> InsertData(ChampNode<TE> n, uint bit, in TE e, Owner? o)
    {
        var idx = Index(n.DataMap, bit);
        if (!Owned(n, o)) return new(n.DataMap | bit, n.NodeMap, InsertAt(n.Data, idx, e), Own(n.Nodes, o), o);
        InsertOwned(ref n.Data, BitOperations.PopCount(n.DataMap), idx, e);
        n.DataMap |= bit;
        MarkSlack(n, o!);
        return n;
    }

    private static ChampNode<TE> RemoveData(ChampNode<TE> n, uint bit, int idx, Owner? o)
    {
        if (!Owned(n, o)) return new(n.DataMap ^ bit, n.NodeMap, RemoveAt(n.Data, idx), Own(n.Nodes, o), o);
        RemoveOwned(n.Data, BitOperations.PopCount(n.DataMap), idx);
        n.DataMap ^= bit;
        MarkSlack(n, o!);
        return n;
    }

    private static ChampNode<TE> DataToNode(ChampNode<TE> n, uint bit, int dataIdx, ChampNode<TE> sub, Owner? o)
    {
        var nodeIdx = Index(n.NodeMap, bit);
        if (!Owned(n, o))
            return new(n.DataMap ^ bit, n.NodeMap | bit, RemoveAt(n.Data, dataIdx), InsertAt(n.Nodes, nodeIdx, sub), o);
        RemoveOwned(n.Data, BitOperations.PopCount(n.DataMap), dataIdx);
        InsertOwned(ref n.Nodes, BitOperations.PopCount(n.NodeMap), nodeIdx, sub);
        n.DataMap ^= bit;
        n.NodeMap |= bit;
        MarkSlack(n, o!);
        return n;
    }

    private static ChampNode<TE> NodeToData(ChampNode<TE> n, uint bit, int nodeIdx, in TE e, Owner? o)
    {
        var dataIdx = Index(n.DataMap, bit);
        if (!Owned(n, o))
            return new(n.DataMap | bit, n.NodeMap ^ bit, InsertAt(n.Data, dataIdx, e), RemoveAt(n.Nodes, nodeIdx), o);
        var entry = e; // e may alias a slot of a child we are about to drop; copy first
        RemoveOwned(n.Nodes, BitOperations.PopCount(n.NodeMap), nodeIdx);
        InsertOwned(ref n.Data, BitOperations.PopCount(n.DataMap), dataIdx, entry);
        n.DataMap |= bit;
        n.NodeMap ^= bit;
        MarkSlack(n, o!);
        return n;
    }

    private static ChampNode<TE> WithCollisions(ChampNode<TE> n, TE[] data, Owner? o)
    {
        if (!Owned(n, o)) return new(0, 0, data, [], o);
        n.Data = data;
        return n;
    }

    private static ChampNode<TE> Merge(in TE e1, uint h1, in TE e2, uint h2, int shift, Owner? o)
    {
        if (shift >= CollisionShift) return new(0, 0, [e1, e2], [], o);
        uint b1 = Bit(h1, shift), b2 = Bit(h2, shift);
        if (b1 == b2) return new(0, b1, [], [Merge(e1, h1, e2, h2, shift + Bits, o)], o);
        return new(b1 | b2, 0, b1 < b2 ? [e1, e2] : [e2, e1], [], o);
    }

    // ----------------------------------------------------------------------------------------- insert

    /// <summary>Inserts or replaces. Returns the (possibly same, if mutated in place) node.</summary>
    public static ChampNode<TE> Set(ChampNode<TE> n, in TE e, K key, uint hash, int shift, Owner? o, ref bool added)
    {
        if (shift >= CollisionShift)
        {
            var d = n.Data;
            for (var i = 0; i < d.Length; i++)
                if (H.Eq(TE.KeyOf(in d[i]), key)) return ReplaceData(n, i, TE.Replaced(in d[i], in e), o);
            added = true;
            return WithCollisions(n, InsertAt(d, d.Length, e), o);
        }

        var bit = Bit(hash, shift);
        if ((n.DataMap & bit) != 0)
        {
            var idx = Index(n.DataMap, bit);
            ref readonly var cur = ref n.Data[idx];
            var ck = TE.KeyOf(in cur);
            if (H.Eq(ck, key)) return ReplaceData(n, idx, TE.Replaced(in cur, in e), o);
            added = true;
            return DataToNode(n, bit, idx, Merge(in cur, HashOf(ck), in e, hash, shift + Bits, o), o);
        }
        if ((n.NodeMap & bit) != 0)
        {
            var idx = Index(n.NodeMap, bit);
            var child = n.Nodes[idx];
            var nc = Set(child, in e, key, hash, shift + Bits, o, ref added);
            return ReferenceEquals(nc, child) ? n : ReplaceNode(n, idx, nc, o);
        }
        added = true;
        return InsertData(n, bit, e, o);
    }

    /// <summary>
    /// Transient-only, iterative: returns a mutable ref to the entry for <paramref name="key"/>, inserting
    /// <c>TE.FromKey(key)</c> if absent. Every node on the path is owned by <paramref name="o"/> afterwards.
    /// </summary>
    public static ref TE GetOrAdd(ref ChampNode<TE> slot, K key, Owner o, out bool exists)
        => ref GetOrAdd(ref slot, new KeyProbe<K, H>(key, HashOf(key)), o, out exists);

    // ----------------------------------------------------------------------------------------- remove

    /// <summary>
    /// Removes <paramref name="key"/>. Returns <paramref name="n"/> untouched (no allocation) when absent.
    /// Non-root results with exactly one entry and no children are inlined by the parent (canonical form).
    /// </summary>
    public static ChampNode<TE> Remove(ChampNode<TE> n, K key, uint hash, int shift, Owner? o, ref bool removed)
        => Remove(n, new KeyProbe<K, H>(key, hash), shift, o, ref removed);

    /// <summary>
    /// Probe-generic walks live in a nested generic *class* rather than as generic methods. When K is a reference type
    /// the code is shared (__Canon); a generic method's dictionary is then resolved via a runtime helper
    /// (CORINFO_HELP_RUNTIMEHANDLE_METHOD) inside the loop, whereas class type arguments travel with the class context.
    /// </summary>
    internal static class WithProbe<TP> where TP : IKeyProbe<K>, allows ref struct
    {
        public static ref TE FindAt(ChampNode<TE> n, scoped TP probe, int shift)
        {
            var hash = probe.Hash;
            while (true)
            {
                if (shift >= CollisionShift)
                {
                    var d = n.Data;
                    for (var i = 0; i < d.Length; i++)
                        if (probe.Matches(TE.KeyOf(in d[i]))) return ref d[i];
                    return ref Unsafe.NullRef<TE>();
                }
                var bit = Bit(hash, shift);
                if ((n.DataMap & bit) != 0)
                {
                    ref var e = ref n.Data[Index(n.DataMap, bit)];
                    if (probe.Matches(TE.KeyOf(in e))) return ref e;
                    return ref Unsafe.NullRef<TE>();
                }
                if ((n.NodeMap & bit) == 0) return ref Unsafe.NullRef<TE>();
                n = n.Nodes[Index(n.NodeMap, bit)];
                shift += Bits;
            }
        }

        public static ref TE GetOrAdd(ref ChampNode<TE> slot, scoped TP probe, Owner o, out bool exists)
        {
            var hash = probe.Hash;
            var shift = 0;
            while (true)
            {
                var n = slot;
                if (!Owned(n, o))
                {
                    n = new ChampNode<TE>(n.DataMap, n.NodeMap, Copy(n.Data), Copy(n.Nodes), o);
                    slot = n;
                }

                if (shift >= CollisionShift)
                {
                    var d = n.Data;
                    for (var i = 0; i < d.Length; i++)
                        if (probe.Matches(TE.KeyOf(in d[i]))) { exists = true; return ref d[i]; }
                    exists = false;
                    n.Data = InsertAt(d, d.Length, TE.FromKey(probe.ToKey()));
                    return ref n.Data[d.Length];
                }

                var bit = Bit(hash, shift);
                if ((n.DataMap & bit) != 0)
                {
                    var idx = Index(n.DataMap, bit);
                    ref var cur = ref n.Data[idx];
                    var ck = TE.KeyOf(in cur);
                    if (probe.Matches(ck)) { exists = true; return ref cur; }
                    exists = false;
                    var sub = Merge(in cur, HashOf(ck), TE.FromKey(probe.ToKey()), hash, shift + Bits, o);
                    DataToNode(n, bit, idx, sub, o);
                    return ref FindAt(sub, probe, shift + Bits);
                }
                if ((n.NodeMap & bit) != 0)
                {
                    slot = ref n.Nodes[Index(n.NodeMap, bit)];
                    shift += Bits;
                    continue;
                }
                exists = false;
                InsertData(n, bit, TE.FromKey(probe.ToKey()), o);
                return ref n.Data[Index(n.DataMap, bit)];
            }
        }

        public static ChampNode<TE> Remove(ChampNode<TE> n, scoped TP probe, int shift, Owner? o, ref bool removed)
        {
            if (shift >= CollisionShift)
            {
                var d = n.Data;
                for (var i = 0; i < d.Length; i++)
                {
                    if (!probe.Matches(TE.KeyOf(in d[i]))) continue;
                    removed = true;
                    return WithCollisions(n, RemoveAt(d, i), o);
                }
                return n;
            }

            var bit = Bit(probe.Hash, shift);
            if ((n.DataMap & bit) != 0)
            {
                var idx = Index(n.DataMap, bit);
                if (!probe.Matches(TE.KeyOf(in n.Data[idx]))) return n;
                removed = true;
                return RemoveData(n, bit, idx, o);
            }
            if ((n.NodeMap & bit) != 0)
            {
                var idx = Index(n.NodeMap, bit);
                var child = n.Nodes[idx];
                var nc = Remove(child, probe, shift + Bits, o, ref removed);
                if (!removed) return n;
                // Collision nodes (incl. one passed up from deeper) have empty bitmaps and exact-size arrays. A bitmap
                // node with DataMap == 0 is non-empty only via NodeMap, which fails the singleton test below anyway.
                var childData = nc.DataMap == 0 ? nc.Data.Length : BitOperations.PopCount(nc.DataMap);
                if (nc.NodeMap == 0 && childData == 1)
                {
                    // Sub-tree collapsed to a single entry: hoist it. If we'd become a singleton chain link too,
                    // pass the singleton straight up (the root never does this as its bit layout is level 0).
                    if (shift > 0 && n.DataMap == 0 && BitOperations.PopCount(n.NodeMap) == 1) return nc;
                    return NodeToData(n, bit, idx, in nc.Data[0], o);
                }
                return ReferenceEquals(nc, child) ? n : ReplaceNode(n, idx, nc, o);
            }
            return n;
        }
    }
}

/// <summary>
/// Allocation-free depth-first CHAMP iterator (max depth 8: 7 levels + collision node). Sizes come from the bitmaps,
/// so it is also correct over owned (transient) nodes whose arrays still carry spare capacity.
/// </summary>
internal struct ChampIterator<TE>
{
    private Buffer8<ChampNode<TE>> _stack;
    private Buffer8<int> _next;
    private int _depth;
    private TE[] _data;
    private int _len;
    private int _i;

    /// <summary>Entries in a node: bitmap nodes count their DataMap; collision nodes (both maps 0) are exact-size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DataCount(ChampNode<TE> n)
        => (n.DataMap | n.NodeMap) != 0 ? BitOperations.PopCount(n.DataMap) : n.Data.Length;

    public ChampIterator(ChampNode<TE>? root)
    {
        _data = root?.Data ?? [];
        _len = root is null ? 0 : DataCount(root);
        _i = -1;
        _depth = -1;
        if (root is not null)
        {
            _depth = 0;
            _stack[0] = root;
            _next[0] = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (++_i < _len) return true;
        return MoveNextNode();
    }

    private bool MoveNextNode()
    {
        while (_depth >= 0)
        {
            var n = _stack[_depth];
            var k = _next[_depth];
            if (k < BitOperations.PopCount(n.NodeMap))
            {
                _next[_depth] = k + 1;
                var c = n.Nodes[k];
                _depth++;
                _stack[_depth] = c;
                _next[_depth] = 0;
                _data = c.Data;
                _len = DataCount(c);
                _i = 0;
                if (_len != 0) return true;
                continue;
            }
            _stack[_depth] = null!;
            _depth--;
        }
        _i = 0;
        _len = 0;
        _data = [];
        return false;
    }

    public readonly ref TE Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _data[_i];
    }
}

/// <summary>
/// Span-based CHAMP iteration for the duck-typed <c>ref struct</c> enumerators (the heap-safe
/// <see cref="ChampIterator{TE}"/> serves the <c>IEnumerable</c> path). Shaped for the JIT rather than for brevity:
/// <list type="bullet">
/// <item>The current node's entries are a <see cref="ReadOnlySpan{T}"/>: within a node, <see cref="MoveNext"/> is an
/// increment and a compare.</item>
/// <item>The DFS stack is eight explicit fields, not an <c>InlineArray</c>: indexing an inline array with a variable
/// takes the field's address, which makes the whole enumerator address-exposed and keeps every field (span, index)
/// in memory. The top of the stack is cached, so the depth <c>switch</c> runs only on a pop.</item>
/// <item>The node step is inlined into the caller: no <c>ref this</c> call, and every reference write targets a
/// stack local, so there are no GC write barriers (the heap-safe iterator pays one per push, pop and node).</item>
/// <item>Child indices are packed one byte per level into a <c>ulong</c> (depth &lt;= 8: 7 levels + collision).</item>
/// </list>
/// Measured on 60k ints: ~7 vs ~9.5 ns per element; a recursive walk (no enumerator) is ~5.5.
/// </summary>
internal ref struct ChampSpanIterator<TE>
{
    private ReadOnlySpan<TE> _span;
    private int _i;
    private int _depth;
    private ulong _next;
    private ChampNode<TE>? _top;
    private ChampNode<TE>? _n0, _n1, _n2, _n3, _n4, _n5, _n6, _n7;

    /// <summary>Entries in a node: bitmap nodes count their DataMap (owned nodes may carry spare capacity);
    /// collision nodes (both maps 0) are exact-size.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DataCount(ChampNode<TE> n)
        => (n.DataMap | n.NodeMap) != 0 ? BitOperations.PopCount(n.DataMap) : n.Data.Length;

    public ChampSpanIterator(ChampNode<TE>? root)
    {
        this = default;
        _i = -1;
        _depth = -1;
        if (root is not null)
        {
            _depth = 0;
            _n0 = _top = root;
            _span = new ReadOnlySpan<TE>(root.Data, 0, DataCount(root));
        }
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
        return NextNode();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool NextNode()
    {
        while (_depth >= 0)
        {
            var n = _top!;
            int shift = _depth << 3, k = (int)(_next >> shift) & 0xFF;
            if (k < BitOperations.PopCount(n.NodeMap))
            {
                _next += 1UL << shift;
                var c = n.Nodes[k];
                _depth++;
                Set(_depth, c);
                _top = c;
                _next &= ~(0xFFUL << (_depth << 3));
                var len = DataCount(c);
                if (len != 0)
                {
                    _span = new ReadOnlySpan<TE>(c.Data, 0, len);
                    _i = 0;
                    return true;
                }
                continue;
            }
            if (--_depth >= 0) _top = Get(_depth);
        }
        _span = default;
        _i = -1;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ChampNode<TE> Get(int d) => d switch
    {
        0 => _n0!, 1 => _n1!, 2 => _n2!, 3 => _n3!, 4 => _n4!, 5 => _n5!, 6 => _n6!, _ => _n7!,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(int d, ChampNode<TE> n)
    {
        switch (d)
        {
            case 0: _n0 = n; break;
            case 1: _n1 = n; break;
            case 2: _n2 = n; break;
            case 3: _n3 = n; break;
            case 4: _n4 = n; break;
            case 5: _n5 = n; break;
            case 6: _n6 = n; break;
            default: _n7 = n; break;
        }
    }

    public readonly ref readonly TE Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _span[_i];
    }
}

/// <summary>
/// Span iteration over an edge set's storage, for <c>EdgeSet&lt;N&gt;.Enumerator</c>: the inline array (&lt;= 16
/// neighbours) as a span, or a CHAMP set via <see cref="ChampSpanIterator{TE}"/>.
/// <para><c>Adjacency&lt;N&gt;.Enumerator</c> deliberately keeps its smaller original design. Using this iterator there
/// made wide-node enumeration faster only once the DAG's node lookup was moved out of line (inlined into the
/// enumerating loop it spilled the iterator's registers), and that extra call per visited node made Pearce–Kelly
/// searches over narrow nodes ~23% slower (failing probe on a 1,000-node chain, interleaved A/B), with no gain on
/// per-edge reordering.</para>
/// Array mode never touches the CHAMP state (it stays default), so small sets, which are most of them and are
/// enumerated once per node in graph walks, pay only for a span.
/// </summary>
internal ref struct EdgeSpanIterator<N> where N : notnull
{
    private ReadOnlySpan<N> _span;
    private int _i;
    private readonly bool _isChamp;
    private ChampSpanIterator<SetEntry<N>> _champ;

    public EdgeSpanIterator(ReadOnlySpan<N> span)
    {
        this = default;
        _span = span;
        _i = -1;
    }

    public EdgeSpanIterator(ChampNode<SetEntry<N>> root)
    {
        this = default;
        _isChamp = true;
        _champ = new(root);
    }

    /// <summary>From an <c>EdgeSet</c>'s raw storage: null | N[] | CHAMP root.</summary>
    public static EdgeSpanIterator<N> Of(object? storage) => storage switch
    {
        ChampNode<SetEntry<N>> root => new(root),
        _ => new((N[]?)storage),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        if (_isChamp) return _champ.MoveNext();
        var i = _i + 1;
        if (i >= _span.Length) return false;
        _i = i;
        return true;
    }

    public readonly N Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _isChamp ? _champ.Current.Key : _span[_i];
    }
}

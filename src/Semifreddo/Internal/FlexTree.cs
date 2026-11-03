using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Semifreddo.Internal;

// Relaxed Radix Balanced tree (Bagwell & Rompf 2011; L'orange 2014), as used by immer::flex_vector.
// - Every node knows its element Count. All leaves are at the same depth. A node at shift s has children each
//   holding at most 1 << s elements.
// - Branches are FDense (every child but the last is full, so radix indexing works; no size table) or FRelaxed
//   (cumulative child counts stored inline in the node, like immer's relaxed nodes: no extra dependent load per
//   level, and a branch-free SIMD count finds the child). Build/Refresh pick the type from the child counts.
// - Concatenation keeps the search-step invariant (#children <= ceil(slots / 32) + Extra) by repacking only the nodes
//   along the seam, so concat / split are O(log n) with bounded constant work per level.
// - Inserts are B-tree style along one path; a full leaf first rebalances with a neighbour that has room (B*-tree
//   style) and only splits when both neighbours are full, keeping leaves well filled under random inserts.

internal abstract class FNode<T>
{
    public Owner? Owner;
    public int Count;
}

/// <summary>
/// A leaf: an <see cref="FPlainLeaf{T}"/> built as one, or an <see cref="FTail{T}"/> made as a tail. Abstract so that
/// both stay sealed: <see cref="FTree{T}.AsLeaf"/> tells them apart by two pointer compares, where a cast to an open
/// class would call the cast helper for every subclass instance.
/// </summary>
internal abstract class FLeaf<T> : FNode<T>
{
    public Buffer32<T> Items;
}

internal sealed class FPlainLeaf<T> : FLeaf<T>;

/// <summary>
/// A tail made by a persistent edit (never owned). A version whose tail holds <c>Fill</c> elements may claim the next
/// slots (once, by CAS) and write them in place; slots are written once, so every version sharing it keeps its own
/// prefix. It joins the tree as it is once sealed at a length (<c>Fill</c> = ~length, in the same CAS), after which
/// no claim can follow. <c>Count</c> is set only by those sealing it, to the length sealed.
/// </summary>
internal sealed class FTail<T> : FLeaf<T>
{
    public int Fill;
}

internal abstract class FBranch<T> : FNode<T>
{
    public Buffer32<FNode<T>?> C;
    public int Len;
}

/// <summary>All children but the last are full: child index = i &gt;&gt; shift.</summary>
internal sealed class FDense<T> : FBranch<T>;

/// <summary>Sz[j] = elements in children 0..j; Sz[j &gt;= Len] = int.MaxValue (never counted as &lt;= an index).</summary>
internal sealed class FRelaxed<T> : FBranch<T>
{
    public Buffer32<int> Sz;
}

/// <summary>
/// Header: tree + right tail. Owned heads (transients) are mutated in place like owned nodes. The tail, its first
/// <c>TailLen</c> elements, is shared with the heads it came from; null when empty.
/// </summary>
internal sealed class FHead<T>
{
    public Owner? Owner;
    public int Count;
    public int Shift = FTree<T>.Bits;
    public FBranch<T>? Root;
    public int TailLen;
    public FLeaf<T>? Tail;

    public int TreeCount => Count - TailLen;
}

internal static class FTree<T>
{
    public const int Bits = 5;
    public const int Width = 1 << Bits;
    public const int Mask = Width - 1;
    private const int Half = Width / 2;
    private const int Extra = 2; // search-step invariant slack

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Owned(FNode<T> n, Owner? o) => o is not null && ReferenceEquals(n.Owner, o);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearFrom(Span<T> s, int from)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) s[from..].Clear();
    }

    /// <summary>Downcast to the abstract branch via two sealed-type checks (pointer compares, no cast helper).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FBranch<T> AsBranch(FNode<T> n) => n as FDense<T> ?? (FBranch<T>)(FRelaxed<T>)n;

    /// <summary>Downcast to the abstract leaf via two sealed-type checks (pointer compares, no cast helper).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FLeaf<T> AsLeaf(FNode<T> n) => n as FPlainLeaf<T> ?? (FLeaf<T>)(FTail<T>)n;

    /// <summary>The node as a leaf, or null for a branch (two sealed-type checks).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static FLeaf<T>? LeafOrNull(FNode<T>? n) => n as FPlainLeaf<T> ?? (FLeaf<T>?)(n as FTail<T>);

    // ---------------------------------------------------------------------------------------- lookup

    /// <summary>Number of cumulative sizes &lt;= i, i.e. the child holding relative index i.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RelaxedSlot(FRelaxed<T> r, int shift, int i)
    {
        ReadOnlySpan<int> sz = r.Sz;
        if (Vector256.IsHardwareAccelerated)
        {
            var key = Vector256.Create(i);
            return BitOperations.PopCount(Vector256.LessThanOrEqual(Vector256.Create(sz), key).ExtractMostSignificantBits())
                 + BitOperations.PopCount(Vector256.LessThanOrEqual(Vector256.Create(sz[8..]), key).ExtractMostSignificantBits())
                 + BitOperations.PopCount(Vector256.LessThanOrEqual(Vector256.Create(sz[16..]), key).ExtractMostSignificantBits())
                 + BitOperations.PopCount(Vector256.LessThanOrEqual(Vector256.Create(sz[24..]), key).ExtractMostSignificantBits());
        }
        if (Vector128.IsHardwareAccelerated)
        {
            var key = Vector128.Create(i);
            var c = 0;
            for (var k = 0; k < Width; k += 4)
                c += BitOperations.PopCount(Vector128.LessThanOrEqual(Vector128.Create(sz[k..]), key).ExtractMostSignificantBits());
            return c;
        }
        var j = i >> shift; // radix guess is a lower bound
        while (sz[j] <= i) j++;
        return j;
    }

    /// <summary>Selects the child holding relative index <paramref name="i"/> and rebases it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChildIndex(FBranch<T> n, int shift, ref int i)
    {
        if (n is FRelaxed<T> r)
        {
            var k = RelaxedSlot(r, shift, i);
            if (k != 0) i -= r.Sz[k - 1];
            return k;
        }
        var j = i >> shift;
        i -= j << shift;
        return j;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T Ref(FBranch<T> root, int shift, int i)
    {
        FNode<T> x = root;
        while (true)
        {
            if (x is FRelaxed<T> r)
            {
                var k = RelaxedSlot(r, shift, i);
                if (k != 0) i -= r.Sz[k - 1];
                x = r.C[k]!;
            }
            else
            {
                var d = (FDense<T>)x;
                var j = i >> shift;
                i -= j << shift;
                x = d.C[j]!;
            }
            if (shift == Bits) return ref AsLeaf(x).Items[i];
            shift -= Bits;
        }
    }

    // ---------------------------------------------------------------------------------------- construction

    /// <summary>Allocates a branch over <paramref name="children"/> of the type their counts call for.</summary>
    private static FBranch<T> Build(ReadOnlySpan<FNode<T>?> children, int shift, Owner? o)
    {
        int cap = 1 << shift, len = children.Length, total = 0;
        var dense = true;
        for (var j = 0; j < len; j++)
        {
            var c = children[j]!.Count;
            if (j < len - 1 && c != cap) dense = false;
            total += c;
        }
        FBranch<T> b;
        if (dense)
        {
            b = new FDense<T>();
        }
        else
        {
            var r = new FRelaxed<T>();
            FillSizes(r, children);
            b = r;
        }
        b.Owner = o;
        b.Len = len;
        b.Count = total;
        children.CopyTo(b.C);
        return b;
    }

    private static void FillSizes(FRelaxed<T> r, ReadOnlySpan<FNode<T>?> children)
    {
        Span<int> sz = r.Sz;
        var acc = 0;
        for (var j = 0; j < children.Length; j++) sz[j] = acc += children[j]!.Count;
        sz[children.Length..].Fill(int.MaxValue);
    }

    /// <summary>
    /// Recomputes Count (and sizes) of an editable branch after its children changed. Returns the same instance when
    /// its type still fits, otherwise a re-typed copy (dense &lt;-&gt; relaxed).
    /// </summary>
    private static FBranch<T> Refresh(FBranch<T> m, int shift, Owner? o)
    {
        var children = ((ReadOnlySpan<FNode<T>?>)m.C)[..m.Len];
        int cap = 1 << shift, total = 0;
        var dense = true;
        for (var j = 0; j < children.Length; j++)
        {
            var c = children[j]!.Count;
            if (j < children.Length - 1 && c != cap) dense = false;
            total += c;
        }
        if (m is FRelaxed<T> r)
        {
            if (dense) return Build(children, shift, o);
            FillSizes(r, children);
            r.Count = total;
            return r;
        }
        if (!dense) return Build(children, shift, o);
        m.Count = total;
        return m;
    }

    /// <summary>Child <paramref name="j"/> of an editable branch gained one element.</summary>
    private static FBranch<T> ChildGrew(FBranch<T> m, int j, int shift, Owner? o)
    {
        if (m is FRelaxed<T> r)
        {
            Span<int> sz = r.Sz;
            for (var k = j; k < r.Len; k++) sz[k]++;
            r.Count++;
            return r;
        }
        if (j == m.Len - 1) { m.Count++; return m; } // the last child may grow freely in a dense node
        return Refresh(m, shift, o);
    }

    private static FBranch<T> EditBranch(FBranch<T> n, Owner? o)
    {
        if (Owned(n, o)) return n;
        FBranch<T> c;
        if (n is FRelaxed<T> r)
        {
            var rc = new FRelaxed<T>();
            rc.Sz = r.Sz;
            c = rc;
        }
        else
        {
            c = new FDense<T>();
        }
        c.Owner = o;
        c.Count = n.Count;
        c.Len = n.Len;
        c.C = n.C;
        return c;
    }

    private static FLeaf<T> EditLeaf(FLeaf<T> n, Owner? o)
    {
        if (Owned(n, o)) return n;
        var c = new FPlainLeaf<T> { Owner = o, Count = n.Count };
        c.Items = n.Items;
        return c;
    }

    private static FNode<T> NewPath(int shift, FNode<T> node, Owner? o)
    {
        for (var s = Bits; s <= shift; s += Bits)
        {
            var p = new FDense<T> { Owner = o, Len = 1, Count = node.Count };
            p.C[0] = node;
            node = p;
        }
        return node;
    }

    public static (FBranch<T>? Root, int Shift) Collapse(FBranch<T>? root, int shift)
    {
        while (root is not null && shift > Bits && root.Len == 1)
        {
            root = AsBranch(root.C[0]!);
            shift -= Bits;
        }
        return (root, shift);
    }

    // ---------------------------------------------------------------------------------------- edits

    /// <summary>Path-copies (or reuses owned nodes) to the slot at <paramref name="i"/> and returns a mutable ref to it.</summary>
    public static ref T EditRef(ref FBranch<T> root, int shift, int i, Owner? o)
    {
        var n = EditBranch(root, o);
        root = n;
        while (true)
        {
            var j = ChildIndex(n, shift, ref i);
            if (shift == Bits)
            {
                var leaf = AsLeaf(n.C[j]!);
                var el = EditLeaf(leaf, o);
                if (!ReferenceEquals(el, leaf)) n.C[j] = el;
                return ref el.Items[i];
            }
            var child = AsBranch(n.C[j]!);
            var ec = EditBranch(child, o);
            if (!ReferenceEquals(ec, child)) n.C[j] = ec;
            n = ec;
            shift -= Bits;
        }
    }

    /// <summary>Appends a (possibly partial) leaf at the right edge.</summary>
    public static (FBranch<T> Root, int Shift) AppendLeaf(FBranch<T>? root, int shift, FLeaf<T> leaf, Owner? o)
    {
        if (root is null)
        {
            var r = new FDense<T> { Owner = o, Len = 1, Count = leaf.Count };
            r.C[0] = leaf;
            return (r, Bits);
        }
        var res = TryAppend(root, shift, leaf, o);
        if (res is not null) return (res, shift);
        return (Build([root, NewPath(shift, leaf, o)], shift + Bits, o), shift + Bits);
    }

    private static FBranch<T>? TryAppend(FBranch<T> node, int shift, FLeaf<T> leaf, Owner? o)
    {
        var last = node.Len - 1;
        if (shift > Bits)
        {
            var r = TryAppend(AsBranch(node.C[last]!), shift - Bits, leaf, o);
            if (r is not null)
            {
                var m = EditBranch(node, o);
                m.C[last] = r;
                m.Count += leaf.Count;
                if (m is FRelaxed<T> mr) mr.Sz[last] += leaf.Count;
                return m;
            }
        }
        if (node.Len == Width) return null;

        var lastFull = node.C[last]!.Count == 1 << shift;
        var m2 = EditBranch(node, o);
        m2.C[m2.Len] = NewPath(shift - Bits, leaf, o);
        m2.Len++;
        m2.Count += leaf.Count;
        if (m2 is FRelaxed<T> r2) r2.Sz[m2.Len - 1] = m2.Count;
        else if (!lastFull) return Refresh(m2, shift, o);
        return m2;
    }

    // ---------------------------------------------------------------------------------------- split

    /// <summary>Keeps the first <paramref name="n"/> (1..Count) elements of a subtree.</summary>
    public static FNode<T> TakeTree(FNode<T> node, int shift, int n, Owner? o)
    {
        if (n == node.Count) return node;
        if (shift == 0)
        {
            var src = AsLeaf(node);
            var l = new FPlainLeaf<T> { Owner = o, Count = n };
            ((ReadOnlySpan<T>)src.Items)[..n].CopyTo(l.Items);
            return l;
        }
        var inner = AsBranch(node);
        var i = n - 1;
        var j = ChildIndex(inner, shift, ref i);
        var buf = inner.C;
        buf[j] = TakeTree(inner.C[j]!, shift - Bits, i + 1, o);
        return Build(((ReadOnlySpan<FNode<T>?>)buf)[..(j + 1)], shift, o);
    }

    /// <summary>Drops the first <paramref name="n"/> (0..Count-1) elements of a subtree.</summary>
    public static FNode<T> DropTree(FNode<T> node, int shift, int n, Owner? o)
    {
        if (n == 0) return node;
        if (shift == 0)
        {
            var src = AsLeaf(node);
            var l = new FPlainLeaf<T> { Owner = o, Count = src.Count - n };
            ((ReadOnlySpan<T>)src.Items)[n..src.Count].CopyTo(l.Items);
            return l;
        }
        var inner = AsBranch(node);
        var i = n;
        var j = ChildIndex(inner, shift, ref i);
        Buffer32<FNode<T>?> buf = default;
        buf[0] = DropTree(inner.C[j]!, shift - Bits, i, o);
        for (var k = j + 1; k < inner.Len; k++) buf[k - j] = inner.C[k];
        return Build(((ReadOnlySpan<FNode<T>?>)buf)[..(inner.Len - j)], shift, o);
    }

    // ---------------------------------------------------------------------------------------- insert

    /// <summary>
    /// B-tree style insert before relative index <paramref name="i"/> (&lt; node.Count) into a branch at
    /// <paramref name="shift"/>. Copy-on-write along one root-to-leaf path (owned nodes are updated in place), so it
    /// is as thread-safe as SetItem. A full branch splits 17/16 and returns its right half in <paramref name="split"/>.
    /// </summary>
    private static FBranch<T> InsertTree(FBranch<T> node, int shift, int i, T item, Owner? o, out FBranch<T>? split)
    {
        var j = ChildIndex(node, shift, ref i);
        FNode<T> nc;
        FNode<T>? sp;
        if (shift == Bits)
        {
            var leaf = AsLeaf(node.C[j]!);
            if (leaf.Count < Width)
            {
                var nl = EditLeaf(leaf, o);
                Span<T> items = nl.Items;
                items[i..nl.Count].CopyTo(items[(i + 1)..]);
                items[i] = item;
                nl.Count++;
                var m = EditBranch(node, o);
                m.C[j] = nl;
                split = null;
                return ChildGrew(m, j, shift, o);
            }
            if (TryRebalanceLeaves(node, j, i, item, o) is { } rebalanced)
            {
                split = null;
                return rebalanced;
            }
            nc = SplitLeaf(leaf, i, item, o, out var right);
            sp = right;
        }
        else
        {
            nc = InsertTree(AsBranch(node.C[j]!), shift - Bits, i, item, o, out var childSplit);
            sp = childSplit;
            if (sp is null)
            {
                var m = EditBranch(node, o);
                m.C[j] = nc;
                split = null;
                return ChildGrew(m, j, shift, o);
            }
        }

        // Child j split into (nc, sp): adopt the right half, splitting this branch too if it is full.
        if (node.Len < Width)
        {
            var m = EditBranch(node, o);
            m.C[j] = nc;
            for (var k = m.Len; k > j + 1; k--) m.C[k] = m.C[k - 1];
            m.C[j + 1] = sp;
            m.Len++;
            split = null;
            return Refresh(m, shift, o);
        }
        Buffer64<FNode<T>?> all = default;
        var n = 0;
        for (var k = 0; k < j; k++) all[n++] = node.C[k];
        all[n++] = nc;
        all[n++] = sp;
        for (var k = j + 1; k < node.Len; k++) all[n++] = node.C[k];
        var span = ((ReadOnlySpan<FNode<T>?>)all)[..n];
        split = Build(span[(Half + 1)..], shift, o);
        return Build(span[..(Half + 1)], shift, o);
    }

    /// <summary>
    /// B*-style: full leaf <paramref name="j"/> shares the insert with the neighbour that has the most room, splitting
    /// the combined (at most 64) elements evenly so both keep slack for later inserts. Null if both neighbours are full.
    /// </summary>
    private static FBranch<T>? TryRebalanceLeaves(FBranch<T> p, int j, int i, T item, Owner? o)
    {
        int nb = -1, room = 0;
        if (j > 0 && Width - p.C[j - 1]!.Count > room) { nb = j - 1; room = Width - p.C[nb]!.Count; }
        if (j + 1 < p.Len && Width - p.C[j + 1]!.Count > room) nb = j + 1;
        if (nb < 0) return null;

        var a = Math.Min(j, nb);
        var la = AsLeaf(p.C[a]!);
        var lb = AsLeaf(p.C[a + 1]!);
        var pos = (a == j ? 0 : la.Count) + i;
        var total = la.Count + lb.Count + 1;

        Buffer64<T> tmp = default;
        Span<T> t = tmp;
        ((ReadOnlySpan<T>)la.Items)[..la.Count].CopyTo(t);
        ((ReadOnlySpan<T>)lb.Items)[..lb.Count].CopyTo(t[la.Count..]);
        t[pos..(total - 1)].CopyTo(t[(pos + 1)..]);
        t[pos] = item;

        var leftN = (total + 1) >> 1;
        var na = new FPlainLeaf<T> { Owner = o, Count = leftN };
        var nb2 = new FPlainLeaf<T> { Owner = o, Count = total - leftN };
        t[..leftN].CopyTo(na.Items);
        t[leftN..total].CopyTo(nb2.Items);

        var m = EditBranch(p, o);
        m.C[a] = na;
        m.C[a + 1] = nb2;
        return Refresh(m, Bits, o);
    }

    private static FLeaf<T> SplitLeaf(FLeaf<T> leaf, int i, T item, Owner? o, out FLeaf<T> right)
    {
        ReadOnlySpan<T> src = leaf.Items;
        var l = new FPlainLeaf<T> { Owner = o, Count = Half + 1 };
        var r = new FPlainLeaf<T> { Owner = o, Count = Half };
        Span<T> ls = l.Items, rs = r.Items;
        if (i <= Half)
        {
            src[..i].CopyTo(ls);
            ls[i] = item;
            src[i..Half].CopyTo(ls[(i + 1)..]);
            src[Half..].CopyTo(rs);
        }
        else
        {
            var k = i - (Half + 1);
            src[..(Half + 1)].CopyTo(ls);
            src[(Half + 1)..i].CopyTo(rs);
            rs[k] = item;
            src[i..].CopyTo(rs[(k + 1)..]);
        }
        right = r;
        return l;
    }

    // ---------------------------------------------------------------------------------------- remove

    private const int MinFill = Width / 4; // underflow threshold for leaves (elements) and branches (children)

    /// <summary>
    /// Removes up to <paramref name="k"/> elements starting at relative index <paramref name="i"/>, limited to the
    /// single leaf holding <paramref name="i"/> (the count actually removed is returned in <paramref name="removed"/>).
    /// Copy-on-write along one path (owned nodes in place). Underfull leaves/branches merge with (or rebalance
    /// against) a neighbour; empty children are dropped, so the result may have Len 0 for the parent to discard.
    /// </summary>
    private static FBranch<T> RemoveTree(FBranch<T> node, int shift, int i, int k, Owner? o, out int removed)
    {
        var j = ChildIndex(node, shift, ref i);
        var m = EditBranch(node, o);
        if (shift == Bits)
        {
            var leaf = AsLeaf(node.C[j]!);
            removed = Math.Min(k, leaf.Count - i);
            var left = leaf.Count - removed;
            if (left == 0)
            {
                RemoveChild(m, j);
                return Refresh(m, shift, o);
            }
            var nl = EditLeaf(leaf, o);
            Span<T> items = nl.Items;
            items[(i + removed)..nl.Count].CopyTo(items[i..]);
            ClearFrom(items, left);
            nl.Count = left;
            m.C[j] = nl;
            if (left < MinFill) FixLeafUnderflow(m, j, o);
            return Refresh(m, shift, o);
        }

        var nc = RemoveTree(AsBranch(node.C[j]!), shift - Bits, i, k, o, out removed);
        if (nc.Len == 0)
        {
            RemoveChild(m, j);
        }
        else
        {
            m.C[j] = nc;
            if (nc.Len < MinFill) FixBranchUnderflow(m, j, shift - Bits, o);
        }
        return Refresh(m, shift, o);
    }

    private static void RemoveChild(FBranch<T> m, int j)
    {
        for (var q = j; q < m.Len - 1; q++) m.C[q] = m.C[q + 1];
        m.C[--m.Len] = null;
    }

    /// <summary>Picks the neighbour of child <paramref name="j"/> with the fewest slots (-1 if none).</summary>
    private static int SmallerNeighbour(FBranch<T> m, int j, Func<FNode<T>, int> slots)
    {
        var nb = -1;
        if (j > 0) nb = j - 1;
        if (j + 1 < m.Len && (nb < 0 || slots(m.C[j + 1]!) < slots(m.C[nb]!))) nb = j + 1;
        return nb;
    }

    /// <summary>Leaf <paramref name="j"/> of editable leaf-parent <paramref name="m"/> is underfull: merge with a
    /// neighbour if the pair fits one leaf, otherwise split the pair's elements evenly.</summary>
    private static void FixLeafUnderflow(FBranch<T> m, int j, Owner? o)
    {
        var nb = SmallerNeighbour(m, j, static x => x.Count);
        if (nb < 0) return;
        var a = Math.Min(j, nb);
        var la = AsLeaf(m.C[a]!);
        var lb = AsLeaf(m.C[a + 1]!);
        var total = la.Count + lb.Count;
        if (total <= Width)
        {
            FLeaf<T> merged;
            if (Owned(la, o))
            {
                merged = la; // transient: append the neighbour in place
            }
            else
            {
                merged = new FPlainLeaf<T> { Owner = o, Count = la.Count };
                ((ReadOnlySpan<T>)la.Items)[..la.Count].CopyTo(merged.Items);
            }
            ((ReadOnlySpan<T>)lb.Items)[..lb.Count].CopyTo(((Span<T>)merged.Items)[merged.Count..]);
            merged.Count = total;
            m.C[a] = merged;
            RemoveChild(m, a + 1);
            return;
        }
        Buffer64<T> tmp = default;
        Span<T> t = tmp;
        ((ReadOnlySpan<T>)la.Items)[..la.Count].CopyTo(t);
        ((ReadOnlySpan<T>)lb.Items)[..lb.Count].CopyTo(t[la.Count..]);
        var leftN = (total + 1) >> 1;
        var na = new FPlainLeaf<T> { Owner = o, Count = leftN };
        var nb2 = new FPlainLeaf<T> { Owner = o, Count = total - leftN };
        t[..leftN].CopyTo(na.Items);
        t[leftN..total].CopyTo(nb2.Items);
        m.C[a] = na;
        m.C[a + 1] = nb2;
    }

    /// <summary>Branch child <paramref name="j"/> (at <paramref name="childShift"/>) has few children: merge it with
    /// a neighbour when their children fit one branch.</summary>
    private static void FixBranchUnderflow(FBranch<T> m, int j, int childShift, Owner? o)
    {
        var nb = SmallerNeighbour(m, j, static x => AsBranch(x).Len);
        if (nb < 0) return;
        var a = Math.Min(j, nb);
        var ba = AsBranch(m.C[a]!);
        var bb = AsBranch(m.C[a + 1]!);
        if (ba.Len + bb.Len > Width) return;
        var buf = ba.C;
        for (var q = 0; q < bb.Len; q++) buf[ba.Len + q] = bb.C[q];
        m.C[a] = Build(((ReadOnlySpan<FNode<T>?>)buf)[..(ba.Len + bb.Len)], childShift, o);
        RemoveChild(m, a + 1);
    }

    // ---------------------------------------------------------------------------------------- concat

    public static (FBranch<T> Root, int Shift) Concat(FBranch<T> a, int sa, FBranch<T> b, int sb, Owner? o)
    {
        var m = Merge(a, sa, b, sb, o);
        var s = Math.Max(sa, sb) + Bits;
        if (m.Len == 1)
        {
            var (cr, cs) = Collapse(AsBranch(m.C[0]!), s - Bits);
            return (cr!, cs);
        }
        return (m, s);
    }

    /// <summary>Merges two subtrees; returns a branch at shift max(ls, rs) + 5 with one or two children.</summary>
    private static FBranch<T> Merge(FNode<T> left, int ls, FNode<T> right, int rs, Owner? o)
    {
        if (ls == 0 && rs == 0)
        {
            var l = AsLeaf(left);
            var r = AsLeaf(right);
            if (l.Count + r.Count <= Width)
            {
                var m = new FPlainLeaf<T> { Owner = o, Count = l.Count + r.Count };
                ((ReadOnlySpan<T>)l.Items)[..l.Count].CopyTo(m.Items);
                ((ReadOnlySpan<T>)r.Items)[..r.Count].CopyTo(((Span<T>)m.Items)[l.Count..]);
                return Build([m], Bits, o);
            }
            return Build([l, r], Bits, o);
        }

        Buffer64<FNode<T>?> all = default;
        int n = 0, s;
        if (ls > rs)
        {
            var l = AsBranch(left);
            var mid = Merge(l.C[l.Len - 1]!, ls - Bits, right, rs, o);
            for (var k = 0; k < l.Len - 1; k++) all[n++] = l.C[k];
            for (var k = 0; k < mid.Len; k++) all[n++] = mid.C[k];
            s = ls;
        }
        else if (ls < rs)
        {
            var r = AsBranch(right);
            var mid = Merge(left, ls, r.C[0]!, rs - Bits, o);
            for (var k = 0; k < mid.Len; k++) all[n++] = mid.C[k];
            for (var k = 1; k < r.Len; k++) all[n++] = r.C[k];
            s = rs;
        }
        else
        {
            var l = AsBranch(left);
            var r = AsBranch(right);
            var mid = Merge(l.C[l.Len - 1]!, ls - Bits, r.C[0]!, rs - Bits, o);
            for (var k = 0; k < l.Len - 1; k++) all[n++] = l.C[k];
            for (var k = 0; k < mid.Len; k++) all[n++] = mid.C[k];
            for (var k = 1; k < r.Len; k++) all[n++] = r.C[k];
            s = ls;
        }

        // `all` holds the nodes (at shift s - 5) that become children of up to two branches at shift s.
        Rebalance(ref all, ref n, s - Bits, o);
        var span = ((ReadOnlySpan<FNode<T>?>)all)[..n];
        if (n <= Width) return Build([Build(span, s, o)], s + Bits, o);
        return Build([Build(span[..Width], s, o), Build(span[Width..], s, o)], s + Bits, o);
    }

    private static int Slots(FNode<T> x, int shift) => shift == 0 ? x.Count : AsBranch(x).Len;

    /// <summary>
    /// Enforces the search-step invariant over the seam nodes: if there are more than ceil(slots/32) + Extra of them,
    /// keep the leading full ones and repack the rest densely (elements for leaves, child pointers for branches).
    /// </summary>
    private static void Rebalance(ref Buffer64<FNode<T>?> all, ref int n, int cs, Owner? o)
    {
        var slots = 0;
        for (var k = 0; k < n; k++) slots += Slots(all[k]!, cs);
        var optimal = (slots + Mask) >> Bits;
        if (n <= optimal + Extra) return;

        var start = 0;
        while (start < n && Slots(all[start]!, cs) == Width) start++;

        Buffer64<FNode<T>?> output = default;
        var on = 0;
        for (var k = 0; k < start; k++) output[on++] = all[k];

        if (cs == 0)
        {
            FLeaf<T>? cur = null;
            for (var k = start; k < n; k++)
            {
                var src = AsLeaf(all[k]!);
                var items = ((ReadOnlySpan<T>)src.Items)[..src.Count];
                while (!items.IsEmpty)
                {
                    if (cur is null || cur.Count == Width) { cur = new FPlainLeaf<T> { Owner = o }; output[on++] = cur; }
                    var take = Math.Min(Width - cur.Count, items.Length);
                    items[..take].CopyTo(((Span<T>)cur.Items)[cur.Count..]);
                    cur.Count += take;
                    items = items[take..];
                }
            }
        }
        else
        {
            Buffer32<FNode<T>?> cur = default;
            var len = 0;
            for (var k = start; k < n; k++)
            {
                var src = AsBranch(all[k]!);
                for (var q = 0; q < src.Len; q++)
                {
                    cur[len++] = src.C[q];
                    if (len == Width)
                    {
                        output[on++] = Build(cur, cs, o);
                        len = 0;
                    }
                }
            }
            if (len != 0) output[on++] = Build(((ReadOnlySpan<FNode<T>?>)cur)[..len], cs, o);
        }
        all = output;
        n = on;
    }

    // ---------------------------------------------------------------------------------------- header ops
    // Each op returns the resulting head; an owned head (transient) is updated in place. A tail is written in place
    // only when owned (a plain leaf whose Count is the tail length) or claimed by a persistent append (an FTail);
    // otherwise it is copied first.

    public static FHead<T> EditHead(FHead<T> h, Owner? o)
    {
        if (o is not null && ReferenceEquals(h.Owner, o)) return h;
        return new FHead<T> { Owner = o, Count = h.Count, Shift = h.Shift, Root = h.Root, Tail = h.Tail, TailLen = h.TailLen };
    }

    /// <summary>A new tail holding <paramref name="len"/> elements (the first <paramref name="len"/> of
    /// <paramref name="items"/>, if any) for an edit by <paramref name="o"/>: owned by a transient, else claimable.</summary>
    private static FLeaf<T> NewTail(ReadOnlySpan<T> items, int len, Owner? o)
    {
        FLeaf<T> t = o is null ? new FTail<T> { Fill = len } : new FPlainLeaf<T> { Owner = o, Count = len };
        items.CopyTo(t.Items);
        return t;
    }

    /// <summary>Sets the length of a tail this edit owns or has just made.</summary>
    private static void SetTailLength(FLeaf<T> t, int len)
    {
        if (t is FTail<T> f) f.Fill = len;
        else t.Count = len;
    }

    /// <summary>
    /// A tail with <paramref name="n"/> free slots after the head's elements: the owned tail, the shared one when a
    /// persistent append claims those slots, else a copy. The caller writes the slots and takes the tail.
    /// </summary>
    private static FLeaf<T> GrowTail(FHead<T> r, int n, Owner? o)
    {
        var len = r.TailLen;
        var t = r.Tail;
        if (t is not null && Owned(t, o))
        {
            t.Count = len + n;
            return t;
        }
        if (o is null && t is FTail<T> f && f.Fill == len && Interlocked.CompareExchange(ref f.Fill, len + n, len) == len)
            return f;
        return NewTail(t is null ? default : ((ReadOnlySpan<T>)t.Items)[..len], len + n, o);
    }

    /// <summary>The head's (non-empty) tail, made its own to rewrite in place: owned, else a copy that it takes.</summary>
    private static FLeaf<T> OwnTail(FHead<T> r, Owner? o)
    {
        var t = r.Tail!;
        if (Owned(t, o)) return t;
        return r.Tail = NewTail(((ReadOnlySpan<T>)t.Items)[..r.TailLen], r.TailLen, o);
    }

    /// <summary>
    /// The first <paramref name="keep"/> elements of <paramref name="leaf"/> as a tail: in place when owned, shared when
    /// the elements hold no references (so none past them is kept alive), else a copy.
    /// </summary>
    private static FLeaf<T> KeepTail(FLeaf<T> leaf, int keep, Owner? o)
    {
        if (Owned(leaf, o))
        {
            ClearFrom(leaf.Items, keep);
            leaf.Count = keep;
            return leaf;
        }
        return RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? NewTail(((ReadOnlySpan<T>)leaf.Items)[..keep], keep, o)
            : leaf;
    }

    /// <summary>
    /// A tail of <paramref name="len"/> elements as a leaf for the tree: as it is when owned, when this seals it, or when
    /// it was sealed at that length before; else a copy.
    /// </summary>
    private static FLeaf<T> SealTail(FLeaf<T> tail, int len, Owner? o)
    {
        if (tail is FTail<T> t)
        {
            if ((t.Fill == len && Interlocked.CompareExchange(ref t.Fill, ~len, len) == len) || Volatile.Read(ref t.Fill) == ~len)
            {
                t.Count = len;                // each sealer writes the same length before it publishes the tree
                return t;
            }
        }
        else if (Owned(tail, o) || tail.Count == len)
        {
            return tail;                      // an owned tail's Count is its length; an unowned plain one never changes
        }
        var c = new FPlainLeaf<T> { Owner = o, Count = len };
        ((ReadOnlySpan<T>)tail.Items)[..len].CopyTo(c.Items);
        return c;
    }

    private static void FlushTail(FHead<T> r, Owner? o)
    {
        if (r.TailLen == 0) return;
        (r.Root, r.Shift) = AppendLeaf(r.Root, r.Shift, SealTail(r.Tail!, r.TailLen, o), o);
        r.Tail = null;
        r.TailLen = 0;
    }

    /// <summary>The head itself when its tail holds none of other heads' elements, else a copy with a tail of its own.</summary>
    public static FHead<T> TrimExcess(FHead<T> h)
    {
        var tail = h.Tail;
        var len = h.TailLen;
        var own = tail is null || (tail is FTail<T> t
            ? Volatile.Read(ref t.Fill) is var f && (f == len || f == ~len)
            : tail.Count == len);
        if (own) return h;
        var c = NewTail(((ReadOnlySpan<T>)tail!.Items)[..len], len, null);
        return new FHead<T> { Count = h.Count, Shift = h.Shift, Root = h.Root, Tail = c, TailLen = len };
    }

    public static FHead<T> Add(FHead<T>? h, T item, Owner? o)
    {
        var r = h is null ? new FHead<T> { Owner = o } : EditHead(h, o);
        if (r.TailLen == Width) FlushTail(r, o);
        var t = GrowTail(r, 1, o);
        t.Items[r.TailLen++] = item;
        r.Tail = t;
        r.Count++;
        return r;
    }

    public static FHead<T> AddRange(FHead<T>? h, ReadOnlySpan<T> items, Owner? o)
    {
        var r = h is null ? new FHead<T> { Owner = o } : EditHead(h, o);
        var take = Math.Min(Width - r.TailLen, items.Length);
        if (take > 0)
        {
            var t = GrowTail(r, take, o);
            items[..take].CopyTo(((Span<T>)t.Items)[r.TailLen..]);
            r.Tail = t;
            r.TailLen += take;
            r.Count += take;
            items = items[take..];
        }
        if (items.IsEmpty) return r;

        // The tail is full and more follow: it joins the tree, whole leaves come straight from the span, and the last
        // 1..32 elements make the new tail.
        FlushTail(r, o);
        while (items.Length > Width)
        {
            var leaf = new FPlainLeaf<T> { Owner = o, Count = Width };
            items[..Width].CopyTo(leaf.Items);
            (r.Root, r.Shift) = AppendLeaf(r.Root, r.Shift, leaf, o);
            r.Count += Width;
            items = items[Width..];
        }
        r.Tail = NewTail(items, items.Length, o);
        r.TailLen = items.Length;
        r.Count += items.Length;
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T ItemRef(FHead<T> h, int i)
    {
        var tc = h.TreeCount;
        if (i >= tc) return ref h.Tail!.Items[i - tc];
        return ref Ref(h.Root!, h.Shift, i);
    }

    /// <summary>Returns the head to write through (owned/copied) and a mutable ref to element <paramref name="i"/>.</summary>
    public static ref T EditItem(ref FHead<T> h, int i, Owner? o)
    {
        var r = EditHead(h, o);
        h = r;
        var tc = r.TreeCount;
        if (i >= tc) return ref OwnTail(r, o).Items[i - tc];
        return ref EditRef(ref r.Root!, r.Shift, i, o);
    }

    public static FHead<T>? Take(FHead<T> h, int n, Owner? o)
    {
        if (n >= h.Count) return h;
        if (n == 0) return null;
        var r = EditHead(h, o);
        var tc = r.TreeCount;
        if (n > tc)
        {
            r.Tail = KeepTail(r.Tail!, n - tc, o);
            r.TailLen = n - tc;
        }
        else
        {
            if (n < tc) (r.Root, r.Shift) = Collapse(AsBranch(TakeTree(r.Root!, r.Shift, n, o)), r.Shift);
            r.Tail = null;
            r.TailLen = 0;
        }
        r.Count = n;
        return r;
    }

    /// <summary>Drops the last element. With the tail empty, the tree's last leaf first becomes the tail (one cut of the
    /// right spine), so the pops that follow are header copies, as appends are. Null when the vector empties.</summary>
    public static FHead<T>? RemoveLast(FHead<T> h, Owner? o)
    {
        if (h.TailLen > 0 || h.Count == 1) return Take(h, h.Count - 1, o);
        var r = EditHead(h, o);
        FNode<T> n = r.Root!;
        for (var s = r.Shift; s > 0; s -= Bits)
        {
            var b = AsBranch(n);
            n = b.C[b.Len - 1]!;
        }
        var last = AsLeaf(n);
        var keep = r.Count - last.Count;      // the tree without its last leaf: the cut shares every leaf it keeps
        if (keep == 0)
        {
            r.Root = null;
            r.Shift = Bits;
        }
        else
        {
            (r.Root, r.Shift) = Collapse(AsBranch(TakeTree(r.Root!, r.Shift, keep, o)), r.Shift);
        }
        r.TailLen = last.Count - 1;
        r.Tail = r.TailLen == 0 ? null : KeepTail(last, r.TailLen, o);
        r.Count--;
        return r;
    }

    public static FHead<T>? Drop(FHead<T> h, int n, Owner? o)
    {
        if (n == 0) return h;
        if (n >= h.Count) return null;
        var r = EditHead(h, o);
        var tc = r.TreeCount;
        if (n >= tc)
        {
            int k = n - tc, rest = r.TailLen - k;
            var tail = r.Tail!;
            if (Owned(tail, o))
            {
                Span<T> items = tail.Items;
                items.Slice(k, rest).CopyTo(items);
                ClearFrom(items, rest);
                tail.Count = rest;
            }
            else
            {
                r.Tail = NewTail(((ReadOnlySpan<T>)tail.Items).Slice(k, rest), rest, o);
            }
            r.TailLen = rest;
            r.Root = null;
            r.Shift = Bits;
        }
        else
        {
            (r.Root, r.Shift) = Collapse(AsBranch(DropTree(r.Root!, r.Shift, n, o)), r.Shift);
        }
        r.Count -= n;
        return r;
    }

    public static FHead<T>? Concat(FHead<T>? a, FHead<T>? b, Owner? o)
    {
        if (b is null || b.Count == 0) return a;
        if (a is null || a.Count == 0) return b;
        if (b.Root is null) return AddRange(a, ((ReadOnlySpan<T>)b.Tail!.Items)[..b.TailLen], o);

        var r = EditHead(a, o);
        FlushTail(r, o);
        (r.Root, r.Shift) = Concat(r.Root!, r.Shift, b.Root, b.Shift, o);
        r.Tail = b.Tail;                      // shared, as the rest of b is
        r.TailLen = b.TailLen;
        r.Count = a.Count + b.Count;
        return r;
    }

    public static FHead<T> Insert(FHead<T>? h, int index, T item, Owner? o)
    {
        if (h is null || index == h.Count) return Add(h, item, o);
        var r = EditHead(h, o);
        var tc = r.TreeCount;
        if (index >= tc)
        {
            if (r.TailLen < Width)
            {
                var t = OwnTail(r, o);
                Span<T> tail = t.Items;
                var k = index - tc;
                tail[k..r.TailLen].CopyTo(tail[(k + 1)..]);
                tail[k] = item;
                SetTailLength(t, ++r.TailLen);
                r.Count++;
                return r;
            }
            FlushTail(r, o); // index now falls inside the tree's last leaf
        }
        var root = InsertTree(r.Root!, r.Shift, index, item, o, out var sp);
        if (sp is null)
        {
            r.Root = root;
        }
        else
        {
            r.Root = Build([root, sp], r.Shift + Bits, o);
            r.Shift += Bits;
        }
        r.Count++;
        return r;
    }

    /// <summary>
    /// Small ranges take the direct path (one root-to-leaf pass per leaf touched; in place on owned nodes), large
    /// ranges split + concat (O(log n) whatever the count). Each persistent pass copies a path, while transient passes
    /// mostly reuse owned nodes, hence the different cut-overs.
    /// </summary>
    internal static int DirectRemoveMaxPersistent = 96;  // measured cross-over vs split+concat ~100-128 (bench -- removecut)
    internal static int DirectRemoveMaxTransient = 256; // owned nodes make direct passes cheaper; cross-over ~256

    public static FHead<T>? RemoveRange(FHead<T> h, int index, int count, Owner? o)
    {
        if (count == 0) return h;
        if (count == h.Count) return null;
        if (count > (o is null ? DirectRemoveMaxPersistent : DirectRemoveMaxTransient))
        {
            var right = Drop(h, index + count, null);
            var left = index == 0 ? null : Take(h, index, o);
            return Concat(left, right, o);
        }

        var r = EditHead(h, o);
        while (count > 0)
        {
            var tc = r.TreeCount;
            if (index >= tc)
            {
                var t = OwnTail(r, o);
                Span<T> tail = t.Items;
                var p = index - tc;
                tail[(p + count)..r.TailLen].CopyTo(tail[p..]);
                ClearFrom(tail, r.TailLen - count);
                SetTailLength(t, r.TailLen -= count);
                if (r.TailLen == 0) r.Tail = null;
                r.Count -= count;
                break;
            }
            var root = RemoveTree(r.Root!, r.Shift, index, count, o, out var removed);
            r.Count -= removed;
            count -= removed;
            if (root.Len == 0) (r.Root, r.Shift) = (null, Bits);
            else (r.Root, r.Shift) = Collapse(root, r.Shift);
        }
        return r;
    }
}

/// <summary>Yields the leaves of an RRB tree left to right (allocation-free, depth at most 8).</summary>
internal struct FLeafCursor<T>
{
    private Buffer8<FBranch<T>?> _stack;
    private Buffer8<int> _next;
    private int _depth;

    public FLeafCursor(FBranch<T>? root)
    {
        _depth = -1;
        if (root is not null)
        {
            _depth = 0;
            _stack[0] = root;
            _next[0] = 0;
        }
    }

    public FLeaf<T>? Next()
    {
        while (_depth >= 0)
        {
            var n = _stack[_depth]!;
            var k = _next[_depth];
            if (k == n.Len) { _stack[_depth--] = null; continue; }
            _next[_depth] = k + 1;
            var c = n.C[k]!;
            if (FTree<T>.LeafOrNull(c) is { } leaf) return leaf;
            _depth++;
            _stack[_depth] = FTree<T>.AsBranch(c);
            _next[_depth] = 0;
        }
        return null;
    }
}

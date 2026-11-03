using System.Runtime.CompilerServices;

namespace Semifreddo.Internal;

// Radix-balanced tree (B = 5, M = 32) with an off-tree tail, as in immer::vector / Clojure's PersistentVector.
// Nodes store their slots inline ([InlineArray]) so each node is a single GC object (no separate T[]).

internal abstract class VNode<T>
{
    public Owner? Owner;
}

internal sealed class VInner<T> : VNode<T>
{
    public Buffer32<VNode<T>?> C;
}

/// <summary>
/// A leaf: a <see cref="VPlainLeaf{T}"/> built as one, or a <see cref="VTail{T}"/> made as a tail. Abstract so that
/// both stay sealed: <see cref="VTree{T}.AsLeaf"/> tells them apart by two pointer compares, where a cast to an open
/// class would call the cast helper for every subclass instance.
/// </summary>
internal abstract class VLeaf<T> : VNode<T>
{
    public Buffer32<T> Items;
}

internal sealed class VPlainLeaf<T> : VLeaf<T>;

/// <summary>
/// A leaf made as a tail, which appends can claim; it joins the tree as it is once full. Other leaves become tails
/// only by pops, and an append to one of those copies it.
/// </summary>
internal sealed class VTail<T> : VLeaf<T>
{
    /// <summary>
    /// The slots claimed so far: a version whose tail holds <c>Fill</c> elements may claim the next slot (once, by
    /// CAS) and write it in place. Slots are written once, so every version sharing this tail keeps its own prefix.
    /// </summary>
    public int Fill;
}

/// <summary>
/// Persistent vector header. The tail is a leaf shared between versions: the first append to a version claims its
/// tail's next slot, so a persistent push allocates only the new header until the tail fills, when the tail itself
/// joins the tree. The value type <c>ImmVector&lt;T&gt;</c> is a single reference to this, making it tear-free and
/// CAS-able.
/// </summary>
internal sealed class VHead<T>
{
    public int Count;
    public int Shift = VTree<T>.Bits;
    public VInner<T>? Root;
    public VLeaf<T> Tail = null!;
}

internal static class VTree<T>
{
    public const int Bits = 5;
    public const int Width = 1 << Bits;
    public const int Mask = Width - 1;

    /// <summary>Index of the first element held by the tail. Requires <paramref name="count"/> &gt;= 1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TailOffset(int count) => (count - 1) & ~Mask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TailSize(int count) => count == 0 ? 0 : ((count - 1) & Mask) + 1;

    /// <summary>Downcast to the abstract leaf via two sealed-type checks (pointer compares, no cast helper).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VLeaf<T> AsLeaf(VNode<T> n) => n as VPlainLeaf<T> ?? (VLeaf<T>)(VTail<T>)n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VLeaf<T> LeafFor(VInner<T> root, int shift, int index)
    {
        VNode<T> n = root;
        for (var level = shift; level > 0; level -= Bits)
            n = ((VInner<T>)n).C[(index >> level) & Mask]!;
        return AsLeaf(n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Owned(VNode<T> n, Owner? o) => o is not null && ReferenceEquals(n.Owner, o);

    public static VInner<T> EditInner(VInner<T> n, Owner? o)
    {
        if (Owned(n, o)) return n;
        var c = new VInner<T> { Owner = o };
        c.C = n.C;
        return c;
    }

    /// <summary>A tail of its own holding <paramref name="items"/>, claimable by the next append.</summary>
    public static VTail<T> NewTail(ReadOnlySpan<T> items)
    {
        var t = new VTail<T> { Fill = items.Length };
        items.CopyTo(t.Items);
        return t;
    }

    /// <summary>
    /// Appends <paramref name="item"/> to a tail holding <paramref name="used"/> (&lt; 32) elements: in place if this
    /// is the first append to it from a version of that size, else into a copy.
    /// </summary>
    public static VLeaf<T> Append(VLeaf<T> tail, int used, T item)
    {
        if (tail is not VTail<T> t || t.Fill != used || Interlocked.CompareExchange(ref t.Fill, used + 1, used) != used)
        {
            t = new VTail<T> { Fill = used + 1 };
            ((ReadOnlySpan<T>)tail.Items)[..used].CopyTo(t.Items);
        }
        t.Items[used] = item;
        return t;
    }

    /// <summary>
    /// The first <paramref name="keep"/> elements of <paramref name="leaf"/> as a tail. Shared when nothing beyond them
    /// can be kept alive by it: the leaf is full, or <typeparamref name="T"/> holds no references.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VLeaf<T> KeepTail(VLeaf<T> leaf, int keep)
        => keep == Width || !RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? leaf
            : NewTail(((ReadOnlySpan<T>)leaf.Items)[..keep]);

    public static VLeaf<T> EditLeaf(VLeaf<T> n, Owner? o)
    {
        if (Owned(n, o)) return n;
        var c = new VPlainLeaf<T> { Owner = o };
        c.Items = n.Items;
        return c;
    }

    /// <summary>Copies the first <paramref name="used"/> slots and clears the rest (only if T holds references).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyTail(ReadOnlySpan<T> src, Span<T> dst, int used)
    {
        src[..used].CopyTo(dst);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) dst[used..].Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearFrom(Span<T> buf, int from)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) buf[from..].Clear();
    }

    private static VNode<T> NewPath(int level, VNode<T> node, Owner? o)
    {
        while (level > 0)
        {
            var r = new VInner<T> { Owner = o };
            r.C[0] = node;
            node = r;
            level -= Bits;
        }
        return node;
    }

    /// <summary>Appends a full leaf as elements [cnt-32, cnt). Returns the new (root, shift).</summary>
    public static (VInner<T> Root, int Shift) PushLeaf(VInner<T>? root, int shift, int cnt, VLeaf<T> leaf, Owner? o)
    {
        if (root is null)
        {
            var r = new VInner<T> { Owner = o };
            r.C[0] = leaf;
            return (r, Bits);
        }
        if ((cnt >> Bits) > (1 << shift))
        {
            var r = new VInner<T> { Owner = o };
            r.C[0] = root;
            r.C[1] = NewPath(shift, leaf, o);
            return (r, shift + Bits);
        }
        return (PushTail(cnt, shift, root, leaf, o), shift);
    }

    private static VInner<T> PushTail(int cnt, int level, VInner<T> parent, VLeaf<T> leaf, Owner? o)
    {
        var ret = EditInner(parent, o);
        var sub = ((cnt - 1) >> level) & Mask;
        if (level == Bits)
        {
            ret.C[sub] = leaf;
        }
        else
        {
            var child = (VInner<T>?)ret.C[sub];
            ret.C[sub] = child is null
                ? NewPath(level - Bits, leaf, o)
                : PushTail(cnt, level - Bits, child, leaf, o);
        }
        return ret;
    }

    /// <summary>Removes the right-most leaf (elements [cnt-33, cnt-1) become the tail). Returns null if the tree empties.</summary>
    public static VInner<T>? PopTail(int cnt, int level, VInner<T> node, Owner? o)
    {
        var sub = ((cnt - 2) >> level) & Mask;
        if (level > Bits)
        {
            var nc = PopTail(cnt, level - Bits, (VInner<T>)node.C[sub]!, o);
            if (nc is null && sub == 0) return null;
            var ret = EditInner(node, o);
            ret.C[sub] = nc;
            return ret;
        }
        if (sub == 0) return null;
        var r = EditInner(node, o);
        r.C[sub] = null;
        return r;
    }

    /// <summary>Normalises root after a pop: drops a root level that only has one child.</summary>
    public static (VInner<T>? Root, int Shift) Collapse(VInner<T>? root, int shift)
    {
        if (root is null) return (null, Bits);
        while (shift > Bits && root.C[1] is null)
        {
            root = (VInner<T>)root.C[0]!;
            shift -= Bits;
        }
        return (root, shift);
    }

    public static VInner<T> Assoc(int level, VInner<T> node, int index, T item, Owner? o)
    {
        var ret = EditInner(node, o);
        var sub = (index >> level) & Mask;
        if (level == Bits)
        {
            var leaf = EditLeaf(AsLeaf(ret.C[sub]!), o);
            leaf.Items[index & Mask] = item;
            ret.C[sub] = leaf;
        }
        else
        {
            ret.C[sub] = Assoc(level - Bits, (VInner<T>)ret.C[sub]!, index, item, o);
        }
        return ret;
    }

    /// <summary>Returns a mutable reference to slot <paramref name="index"/> (in the tree), path-copying into owned nodes.</summary>
    public static ref T EditRef(ref VInner<T> root, int shift, int index, Owner o)
    {
        var n = EditInner(root, o);
        root = n;
        for (var level = shift; level > Bits; level -= Bits)
        {
            var s = (index >> level) & Mask;
            var child = (VInner<T>)n.C[s]!;
            var ec = EditInner(child, o);
            if (!ReferenceEquals(ec, child)) n.C[s] = ec;
            n = ec;
        }
        var ls = (index >> Bits) & Mask;
        var leaf = AsLeaf(n.C[ls]!);
        var el = EditLeaf(leaf, o);
        if (!ReferenceEquals(el, leaf)) n.C[ls] = el;
        return ref el.Items[index & Mask];
    }

    /// <summary>Keeps tree elements [0, newTailOffset). Returns normalised (root, shift).</summary>
    public static (VInner<T>? Root, int Shift) Trim(VInner<T> root, int shift, int newTailOffset, Owner? o)
    {
        if (newTailOffset == 0) return (null, Bits);
        return Collapse(TrimRight(root, shift, newTailOffset - 1, o), shift);
    }

    private static VInner<T> TrimRight(VInner<T> node, int level, int last, Owner? o)
    {
        var ret = EditInner(node, o);
        var sub = (last >> level) & Mask;
        for (var j = sub + 1; j < Width; j++) ret.C[j] = null;
        if (level > Bits) ret.C[sub] = TrimRight((VInner<T>)ret.C[sub]!, level - Bits, last, o);
        return ret;
    }
}

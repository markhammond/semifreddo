namespace Semifreddo.Internal;

/// <summary>
/// Bounds the scratch a pooled transient keeps between batches. Kept, it saves the next batch allocating; but a
/// <see cref="Dictionary{TKey, TValue}"/> or <see cref="HashSet{T}"/> clears in time proportional to its capacity, so
/// after one large batch every small one would pay for it. On return to the pool, anything grown past
/// <see cref="Limit"/> is trimmed back to <see cref="PooledCapacity"/> (which a hash set rounds up to a prime, still
/// within the limit): one reallocation after a large batch, none in a steady state.
/// </summary>
internal static class Scratch
{
    public const int PooledCapacity = 4096;
    public const int Limit = 2 * PooledCapacity;

    public static void Trim<K, V>(Dictionary<K, V> d) where K : notnull
    {
        if (d.Capacity <= Limit) return;
        d.Clear();
        d.TrimExcess(PooledCapacity);
    }

    public static void Trim<T>(HashSet<T> s)
    {
        if (s.Capacity <= Limit) return;
        s.Clear();
        s.TrimExcess(PooledCapacity);
    }

    public static void Trim<T>(List<T> l)
    {
        if (l.Capacity <= Limit) return;
        l.Clear();
        l.Capacity = PooledCapacity;
    }
}

/// <summary>
/// Shrinks a reusable search set once searches keep using a small part of it. Clearing a set costs its capacity, a
/// search only what it visits: after one large search, every small one would pay for the large one's room. Sixteen
/// searches in a row under an eighth of the capacity shrink it to twice the largest of them (at least 64); a search that
/// fills it again resets the count, so a steady mix of sizes never reallocates.
/// </summary>
internal struct SparseUse
{
    private const int Runs = 16, Floor = 64;
    private int _runs, _peak;

    public bool Clear<K, V>(Dictionary<K, V> d) where K : notnull
    {
        var used = d.Count;
        d.Clear();
        if (!ShouldShrink(used, d.Capacity, out var keep)) return false;
        d.TrimExcess(keep);
        return true;
    }

    public bool Clear<T>(HashSet<T> s)
    {
        var used = s.Count;
        s.Clear();
        if (!ShouldShrink(used, s.Capacity, out var keep)) return false;
        s.TrimExcess(keep);
        return true;
    }

    private bool ShouldShrink(int used, int capacity, out int keep)
    {
        keep = 0;
        if (used == 0) return false;                         // nothing searched: no evidence either way
        if (capacity <= 4 * Floor || used * 8 >= capacity) { _runs = 0; _peak = 0; return false; }
        _peak = Math.Max(_peak, used);
        if (++_runs < Runs) return false;
        keep = Math.Max(Floor, 2 * _peak);
        _runs = 0;
        _peak = 0;
        return true;
    }
}


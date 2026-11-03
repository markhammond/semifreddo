using System.Runtime.InteropServices;

namespace Semifreddo.Internal;

/// <summary>
/// Insertion-ordered set with O(1) add / remove and a dense span view. Callers only add items that are not live
/// (graph semantics guarantee it), so appends need no lookup; the item → position index is built lazily on the first
/// removal, so append-only batches (bulk loads) cost a list append per entry. Removal tombstones; the span getter
/// compacts lazily (O(n) once per burst of removals).
/// </summary>
internal sealed class ChangeLog<T> where T : notnull
{
    private readonly List<T> _items = [];
    private readonly List<bool> _live = [];
    private Dictionary<T, int>? _index; // null until the first removal
    private Dictionary<T, int>? _spare; // the last batch's index, cleared, for the next to reuse
    private int _dead;

    public int Count => _items.Count - _dead;

    /// <summary>Appends <paramref name="item"/>, which must not currently be in the log.</summary>
    public void Add(T item)
    {
        _index?.Add(item, _items.Count);
        _items.Add(item);
        _live.Add(true);
    }

    public bool Remove(T item)
    {
        if (_items.Count == _dead) return false;
        if (_index is null)
        {
            _index = _spare ?? new Dictionary<T, int>(_items.Count);
            _spare = null;
            for (var i = 0; i < _items.Count; i++) if (_live[i]) _index[_items[i]] = i;
        }
        if (!_index.Remove(item, out var at)) return false;
        _live[at] = false;
        _items[at] = default!; // release references
        _dead++;
        return true;
    }

    /// <summary>Empties the log, keeping its capacity (and its index, cleared) for the next batch: no allocation once
    /// warm. A caller that won't keep that capacity skips the log instead (<c>DagBatchOptions.SkipChangeLog</c>).</summary>
    public void Clear()
    {
        _items.Clear();
        _live.Clear();
        if (_index is not null)
        {
            _index.Clear();
            _spare = _index;
        }
        _index = null;
        _dead = 0;
    }

    /// <summary>After <see cref="Clear"/>: bounds the capacity kept for the next batch (see <see cref="Scratch"/>).</summary>
    public void TrimExcess()
    {
        Scratch.Trim(_items);
        Scratch.Trim(_live);
        if (_spare is not null && _spare.Capacity > Scratch.Limit) _spare = null;
    }

    public ReadOnlySpan<T> Items
    {
        get
        {
            if (_dead != 0) Compact();
            return CollectionsMarshal.AsSpan(_items);
        }
    }

    private void Compact()
    {
        var k = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (!_live[i]) continue;
            var x = _items[i];
            _items[k] = x;
            _live[k] = true;
            if (_index is not null) _index[x] = k;
            k++;
        }
        _items.RemoveRange(k, _items.Count - k);
        _live.RemoveRange(k, _live.Count - k);
        _dead = 0;
    }
}

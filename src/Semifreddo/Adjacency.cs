using System.Runtime.CompilerServices;
using Semifreddo.Internal;

namespace Semifreddo;

/// <summary>
/// Read-only adjacency view consumed by topological orders (and any other graph algorithm in this library). Implement
/// it on your own graph type and pass the graph through a generic constraint (<c>where TGraph : IDagAdjacency&lt;N&gt;,
/// allows ref struct</c>): no boxing, no interface enumerators. <see cref="ImmDag{N, A, V}"/>, its transient and its
/// batch scope implement it.
/// </summary>
public interface IDagAdjacency<N> where N : notnull
{
    Adjacency<N> Successors(N node);
    Adjacency<N> Predecessors(N node);
}

/// <summary>
/// A node's neighbours as seen through <see cref="IDagAdjacency{N}"/>. Build one from contiguous storage
/// (<see cref="Adjacency{N}(ReadOnlySpan{N})"/>: allocation-free) or from any collection (falls back to the
/// collection's enumerator). Neighbours must be distinct. Valid only while the underlying storage is unchanged.
/// </summary>
public readonly ref struct Adjacency<N> where N : notnull
{
    private readonly ReadOnlySpan<N> _span;
    private readonly object? _other; // null (span) | ChampNode<SetEntry<N>> | IReadOnlyCollection<N>
    private readonly int _count;

    public Adjacency(ReadOnlySpan<N> neighbours)
    {
        _span = neighbours;
        _count = neighbours.Length;
    }

    public Adjacency(IReadOnlyCollection<N> neighbours)
    {
        _other = neighbours;
        _count = neighbours.Count;
    }

    internal Adjacency(EdgeSet<N> set)
    {
        if (set.TryGetSpan(out var span)) { _span = span; _count = span.Length; }
        else { _other = set.Raw; _count = set.Count; }
    }

    public int Count => _count;
    public bool IsEmpty => _count == 0;

    public Enumerator GetEnumerator() => new(_span, _other);

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<N> _span;
        private readonly IEnumerator<N>? _enumerator;
        private ChampIterator<SetEntry<N>> _set;
        private readonly byte _mode; // 0 span, 1 CHAMP set, 2 collection
        private int _i;

        internal Enumerator(ReadOnlySpan<N> span, object? other)
        {
            _span = span;
            _i = -1;
            switch (other)
            {
                case null: _mode = 0; break;
                case ChampNode<SetEntry<N>> root: _mode = 1; _set = new(root); break;
                default: _mode = 2; _enumerator = ((IReadOnlyCollection<N>)other).GetEnumerator(); break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext() => _mode switch
        {
            0 => ++_i < _span.Length,
            1 => _set.MoveNext(),
            _ => _enumerator!.MoveNext(),
        };

        public readonly N Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _mode switch
            {
                0 => _span[_i],
                1 => _set.Current.Key,
                _ => _enumerator!.Current,
            };
        }
    }
}

namespace Semifreddo;

/// <summary>
/// Library-neutral value-enumeration contract, in BCL types only, so a host can bind Semifreddo's collections to whichever
/// value-LINQ library it prefers (ZLinq, StructLinq, HyperLinq or its own) with one generic adapter and no boxing.
/// Semifreddo takes no dependency on any of them.
/// <list type="bullet">
/// <item><see cref="GetEnumerator"/> is the collection's duck-typed <c>foreach</c> method. It returns a <c>ref struct</c>
/// enumerator that implements <see cref="IEnumerator{T}"/>, so generic code reaches <c>MoveNext</c> / <c>Current</c>
/// through constrained calls (<c>where TEnumerator : struct, IEnumerator&lt;T&gt;, allows ref struct</c>) and the
/// enumerator can never be boxed. The non-generic <see cref="IEnumerable{T}"/> path stays available for plain LINQ.</item>
/// <item><see cref="IReadOnlyCollection{T}.Count"/> is exact and O(1).</item>
/// <item><see cref="TryGetSpan"/> and <see cref="TryCopyTo"/> are optional fast paths. <c>false</c> means "not
/// supported here, enumerate instead", never an error.</item>
/// </list>
/// A host adapter calls these members on a generic <c>TSource : struct, IRefEnumerable&lt;T, TEnumerator&gt;</c>, so
/// each call is a direct (inlinable) call on the struct. Implemented explicitly where it would only clutter the API.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
/// <typeparam name="TEnumerator">The collection's <c>ref struct</c> enumerator.</typeparam>
public interface IRefEnumerable<T, TEnumerator> : IReadOnlyCollection<T>
    where TEnumerator : struct, IEnumerator<T>, allows ref struct
{
    /// <summary>The duck-typed <c>foreach</c> enumerator (allocation-free, cannot be boxed).</summary>
    new TEnumerator GetEnumerator();

    /// <summary>The whole sequence as one contiguous span (true for an empty sequence), when stored that way.</summary>
    bool TryGetSpan(out ReadOnlySpan<T> span);

    /// <summary>
    /// Copies <c>min(destination.Length, Count - index)</c> elements, starting at element <paramref name="index"/>, to the
    /// start of <paramref name="destination"/> faster than enumerating would (e.g. bulk chunk copies or O(log n) random
    /// access). Returns false, copying nothing, when the collection has no such fast path.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Count]</c>.</exception>
    bool TryCopyTo(int index, Span<T> destination);
}

/// <summary>Shared implementations of the <see cref="IRefEnumerable{T, TEnumerator}"/> fast paths.</summary>
internal static class RefEnumerable
{
    /// <summary>Up to this many elements are fetched by index (O(log n) each) rather than by walking chunks.</summary>
    public const int RandomAccessMax = 16;

    /// <summary>Validates <paramref name="index"/> and returns the number of elements to copy.</summary>
    public static int CopyLength(int count, int index, int destinationLength)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)count, nameof(index));
        return Math.Min(destinationLength, count - index);
    }

    /// <summary>Copies the part of <paramref name="chunk"/> inside [start, start + n); true once n elements are written.</summary>
    public static bool CopyChunk<T>(ReadOnlySpan<T> chunk, Span<T> destination, int start, int n, ref int skipped, ref int written)
    {
        var from = start - skipped;
        skipped += chunk.Length;
        if (from >= chunk.Length) return false; // wholly before the range
        var part = chunk[Math.Max(0, from)..];
        if (part.Length > n - written) part = part[..(n - written)];
        part.CopyTo(destination[written..]);
        written += part.Length;
        return written == n;
    }
}

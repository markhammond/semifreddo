using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Semifreddo.Internal;

/// <summary>
/// Edit token. A node whose <c>Owner</c> is reference-equal to a transient's current token is exclusively
/// reachable from that transient and may be mutated in place; any other node is frozen and is copied on write.
/// Freezing a transient (ToPersistent) drops its token; the only work is trimming the spare capacity of nodes the batch
/// grew, so the rest of the structure is never walked.
/// </summary>
internal class Owner;

[InlineArray(32)]
internal struct Buffer32<T>
{
    private T _e0;
}

[InlineArray(8)]
internal struct Buffer8<T>
{
    private T _e0;
}

/// <summary>
/// Throw sites kept out of the methods that check: a method that only throws is never inlined, so its caller stays small
/// enough to inline and builds no message (an interpolated one sets up a handler in the caller's frame) unless it throws.
/// </summary>
internal static class Throw
{
    [DoesNotReturn] public static void IndexOutOfRange() => throw new ArgumentOutOfRangeException("index");
    [DoesNotReturn] public static void Empty() => throw new InvalidOperationException("Collection is empty.");
    [DoesNotReturn] public static void KeyNotFound<K>(K key) => throw new KeyNotFoundException($"Key '{key}' not found.");
    [DoesNotReturn] public static void KeyNotFound() => throw new KeyNotFoundException();
    [DoesNotReturn] public static void Cycle<N>(N[] cycle) => throw new DagCycleException<N>(cycle);
    [DoesNotReturn] public static void Sentinel<N>(N node, string paramName) => throw new ArgumentException($"{node} is the order's 'no node' sentinel.", paramName);
    [DoesNotReturn] public static void NodePresent<N, A>(N id, A alternateKey) => throw new ArgumentException($"Node {id} or alternate key {alternateKey} already present.");
    [DoesNotReturn] public static void CannotReplace<N>(N oldId, N newId) => throw new ArgumentException($"Cannot replace {oldId} with {newId}.");
    [DoesNotReturn] public static void Untracked() => throw new InvalidOperationException("This batch does not record its change log (DagBatchOptions.SkipChangeLog, or TrackChanges = false).");
    [DoesNotReturn] public static void OrderMismatch(int order, int graph) => throw new ArgumentException($"The order holds {order} nodes, the graph {graph}: it must hold exactly the graph's nodes.", "order");
}

[InlineArray(64)]
internal struct Buffer64<T>
{
    private T _e0;
}

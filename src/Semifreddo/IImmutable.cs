namespace Semifreddo;

/// <summary>
/// Marker: an instance never changes after construction (Semifreddo's persistent collections, and any type of yours that
/// guarantees it). Lets consumers share instances instead of copying them (snapshots, undo histories, caches keyed by
/// version). No members; it is a promise.
/// </summary>
public interface IImmutable;

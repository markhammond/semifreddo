using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>One step of a batch: what to do, with its key or index and its value(s).</summary>
public readonly record struct Op(Do Kind, int A = 0, int B = 0, int C = 0, int D = 0);

public enum Do
{
    Get, Item, Has, TryAdd, SetItem, Put, Bump, Add, AddRange, AddFlex, Insert, Remove, RemoveAt, RemoveRange, RemoveLast,
    Take, Skip, Clear,
    AddNode, RemoveNode, SetValue, Rename, Replace, AddEdge, RemoveEdge, Alt, ByAlt, HasEdge,
}

/// <summary>
/// Shared shape of the CRUD partition suites: each partition is a starting shape and a batch, run through
/// <c>Mutate</c> and through a BCL model of the documented semantics. Each step's answer and the count after it
/// must agree; so must the contents, or the exception the model expects, which must leave the source as it was and
/// the next batch on the thread clean. The source never changes, and a later batch never reaches the result.
/// </summary>
internal static class Partition
{
    public static TheoryData<string> Names<T>(Dictionary<string, T> cases)
    {
        var d = new TheoryData<string>();
        foreach (var name in cases.Keys) d.Add(name);
        return d;
    }

    public static string Yes(bool b) => b ? "true" : "false";

    public static string Found<V>(bool found, V value) => found ? $"={value}" : "-";

    /// <summary>Runs <paramref name="model"/> over the steps; null when it throws (the exception is returned).</summary>
    public static List<string>? Expect(Func<List<string>> model, out Exception? thrown)
    {
        try { thrown = null; return model(); }
        catch (Exception e) { thrown = e; return null; }
    }

    public static readonly int[] Many = [.. Enumerable.Range(0, 40)];   // past the CHAMP root; collision nodes for BadKey
}

public record KeyedCase(int[] Start, Op[] Ops, bool Same = false);

// ------------------------------------------------------------------------------------------------------------ ImmMap

public class MapMutationPartitionTests
{
    private static Op Get(int k) => new(Do.Get, k);
    private static Op Item(int k) => new(Do.Item, k);
    private static Op Has(int k) => new(Do.Has, k);
    private static Op TryAdd(int k, int v) => new(Do.TryAdd, k, v);
    private static Op SetItem(int k, int v) => new(Do.SetItem, k, v);
    private static Op Put(int k, int v) => new(Do.Put, k, v);
    private static Op Bump(int k, int by) => new(Do.Bump, k, by);
    private static Op Remove(int k) => new(Do.Remove, k);
    private static readonly Op Clear = new(Do.Clear);
    private static readonly int[] Many = Partition.Many;

    private static readonly Dictionary<string, KeyedCase> Cases = new()
    {
        ["create: absent key, empty map"] = new([], [TryAdd(1, 7)]),
        ["create: absent key, populated map"] = new(Many, [TryAdd(99, 7)]),
        ["create: key present at the start is refused"] = new(Many, [TryAdd(3, 7)], Same: true),
        ["create: key created earlier in the batch is refused"] = new([], [TryAdd(1, 7), TryAdd(1, 8)]),
        ["create: key removed earlier in the batch"] = new(Many, [Remove(3), TryAdd(3, 7)]),
        ["create: after a clear"] = new(Many, [Clear, TryAdd(3, 7)]),
        ["create: by SetItem, the indexer and an upsert"] = new([1], [SetItem(2, 5), Put(3, 6), Bump(4, 9)]),
        ["read: present at the start"] = new(Many, [Get(3), Item(3), Has(3)], Same: true),
        ["read: absent"] = new(Many, [Get(99), Has(99)], Same: true),
        ["read: empty map"] = new([], [Get(1), Has(1)], Same: true),
        ["read: created in the batch"] = new([], [TryAdd(1, 5), Get(1), Item(1)]),
        ["read: removed in the batch"] = new(Many, [Remove(3), Get(3), Has(3)]),
        ["read: the indexer on an absent key throws"] = new(Many, [Put(3, 1), Item(99)]),
        ["update: present at the start"] = new(Many, [SetItem(3, 99), Put(4, 98), Bump(5, 1)]),
        ["update: to the value it holds"] = new(Many, [SetItem(3, 30)]),
        ["update: created in the batch"] = new([], [TryAdd(1, 1), SetItem(1, 2), Bump(1, 3)]),
        ["update: then removed"] = new(Many, [SetItem(3, 1), Remove(3)]),
        ["delete: present at the start"] = new(Many, [Remove(3)]),
        ["delete: absent"] = new(Many, [Remove(99)], Same: true),
        ["delete: empty map"] = new([], [Remove(1)], Same: true),
        ["delete: twice"] = new(Many, [Remove(3), Remove(3)]),
        ["delete: created in the batch"] = new(Many, [TryAdd(99, 1), Remove(99)]),
        ["delete: the last key leaves the empty map"] = new([1], [Remove(1)]),
        ["delete: every key"] = new(Many, [.. Many.Select(Remove)]),
        ["clear: populated"] = new(Many, [Clear]),
        ["clear: empty"] = new([], [Clear], Same: true),
        ["batch: no operations"] = new(Many, [], Same: true),
        ["batch: every operation"] = new(Many, [TryAdd(99, 1), SetItem(3, 2), Bump(98, 3), Remove(4), Get(99), Remove(99), Put(4, 5)]),
    };

    public static TheoryData<string> Partitions => Partition.Names(Cases);

    [Theory, MemberData(nameof(Partitions))] public void IntKeys(string partition) => Run(i => i, Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void StringKeys(string partition) => Run(i => $"k{i}", Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void CollidingKeys(string partition) => Run(i => new BadKey(i), Cases[partition]);

    private static void Run<K>(Func<int, K> key, KeyedCase c) where K : notnull
    {
        var start = c.Start.ToDictionary(key, i => i * 10);
        var source = ImmMap<K, int>.Empty.Mutate(start, static (s, d) => { foreach (var (k, v) in d) s.TryAdd(k, v); });
        var model = new Dictionary<K, int>(start);
        var expected = Partition.Expect(() =>
        {
            var log = new List<string>();
            foreach (var op in c.Ops) log.Add($"{Model(model, op, key)} #{model.Count}");
            return log;
        }, out var thrown);
        if (thrown is not null)
        {
            Assert.Throws(thrown.GetType(), () => source.Mutate(s => { foreach (var op in c.Ops) Apply(s, op, key); }));
            AssertEqual(start, source);
            Assert.True(source.Mutate(static _ => { }).IsSameAs(source));
            Assert.Single(ImmMap<K, int>.Empty.Mutate(s => s.TryAdd(key(1), 1)));
            return;
        }

        var actual = new List<string>();
        var result = source.Mutate((c.Ops, key, actual), static (s, a) =>
        {
            foreach (var op in a.Ops) a.actual.Add($"{Apply(s, op, a.key)} #{s.Count}");
        });
        Assert.Equal(expected, actual);
        AssertEqual(model, result);
        AssertEqual(start, source);
        if (c.Same) Assert.True(result.IsSameAs(source), "a batch that changed nothing made a new version");
        if (model.Count == 0) Assert.True(result.IsSameAs(ImmMap<K, int>.Empty));
        _ = result.Mutate((model, key), static (s, a) =>
        {
            foreach (var k in a.model.Keys) s[k] = -1;
            for (var i = 1000; i < 1100; i++) s[a.key(i)] = i;
            foreach (var k in a.model.Keys) s.Remove(k);
        });
        AssertEqual(model, result);
    }

    private static string Apply<K>(ImmMap<K, int>.Scope s, Op op, Func<int, K> key) where K : notnull
    {
        var k = key(op.A);
        switch (op.Kind)
        {
            case Do.Get: return Partition.Found(s.TryGetValue(k, out var v), v);
            case Do.Item: return $"={s[k]}";
            case Do.Has: return Partition.Yes(s.ContainsKey(k));
            case Do.TryAdd: return Partition.Yes(s.TryAdd(k, op.B));
            case Do.SetItem: return Partition.Yes(s.SetItem(k, op.B));
            case Do.Put: s[k] = op.B; return "";
            case Do.Bump: s.GetValueRefOrAddDefault(k, out var existed) += op.B; return Partition.Yes(existed);
            case Do.Remove: return Partition.Yes(s.Remove(k));
            case Do.Clear: s.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not a map operation");
        }
    }

    private static string Model<K>(Dictionary<K, int> d, Op op, Func<int, K> key) where K : notnull
    {
        var k = key(op.A);
        switch (op.Kind)
        {
            case Do.Get: return Partition.Found(d.TryGetValue(k, out var v), v);
            case Do.Item: return d.TryGetValue(k, out var w) ? $"={w}" : throw new KeyNotFoundException();
            case Do.Has: return Partition.Yes(d.ContainsKey(k));
            case Do.TryAdd: return Partition.Yes(d.TryAdd(k, op.B));
            case Do.SetItem: { var added = !d.ContainsKey(k); d[k] = op.B; return Partition.Yes(added); }
            case Do.Put: d[k] = op.B; return "";
            case Do.Bump: { var existed = d.TryGetValue(k, out var x); d[k] = x + op.B; return Partition.Yes(existed); }
            case Do.Remove: return Partition.Yes(d.Remove(k));
            case Do.Clear: d.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not a map operation");
        }
    }

    private static void AssertEqual<K>(Dictionary<K, int> expected, ImmMap<K, int> actual) where K : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (k, v) in expected) Assert.True(actual.TryGetValue(k, out var a) && a == v, $"{k}: expected {v}");
        Assert.Equal(expected.Count, actual.Count(kv => expected.TryGetValue(kv.Key, out var v) && v == kv.Value));
        Check.Map(actual);
    }
}

// ------------------------------------------------------------------------------------------------------------ ImmSet

public class SetMutationPartitionTests
{
    private static Op Has(int k) => new(Do.Has, k);
    private static Op Add(int k) => new(Do.Add, k);
    private static Op Remove(int k) => new(Do.Remove, k);
    private static readonly Op Clear = new(Do.Clear);
    private static readonly int[] Many = Partition.Many;

    private static readonly Dictionary<string, KeyedCase> Cases = new()
    {
        ["create: absent, empty set"] = new([], [Add(1)]),
        ["create: absent, populated set"] = new(Many, [Add(99)]),
        ["create: present at the start is a no-op"] = new(Many, [Add(3)], Same: true),
        ["create: created earlier in the batch"] = new([], [Add(1), Add(1)]),
        ["create: removed earlier in the batch"] = new(Many, [Remove(3), Add(3)]),
        ["create: after a clear"] = new(Many, [Clear, Add(3)]),
        ["read: present at the start"] = new(Many, [Has(3)], Same: true),
        ["read: absent"] = new(Many, [Has(99)], Same: true),
        ["read: created in the batch"] = new([], [Add(1), Has(1)]),
        ["read: removed in the batch"] = new(Many, [Remove(3), Has(3)]),
        ["delete: present at the start"] = new(Many, [Remove(3)]),
        ["delete: absent"] = new(Many, [Remove(99)], Same: true),
        ["delete: empty set"] = new([], [Remove(1)], Same: true),
        ["delete: twice"] = new(Many, [Remove(3), Remove(3)]),
        ["delete: created in the batch"] = new(Many, [Add(99), Remove(99)]),
        ["delete: the last item leaves the empty set"] = new([1], [Remove(1)]),
        ["delete: every item"] = new(Many, [.. Many.Select(Remove)]),
        ["clear: populated"] = new(Many, [Clear]),
        ["clear: empty"] = new([], [Clear], Same: true),
        ["batch: no operations"] = new(Many, [], Same: true),
    };

    public static TheoryData<string> Partitions => Partition.Names(Cases);

    [Theory, MemberData(nameof(Partitions))] public void IntItems(string partition) => Run(i => i, Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void StringItems(string partition) => Run(i => $"k{i}", Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void CollidingItems(string partition) => Run(i => new BadKey(i), Cases[partition]);

    private static void Run<T>(Func<int, T> item, KeyedCase c) where T : notnull
    {
        var start = c.Start.Select(item).ToHashSet();
        var source = ImmSet<T>.Empty.Mutate(start, static (s, h) => { foreach (var x in h) s.Add(x); });
        var model = new HashSet<T>(start);
        var expected = Partition.Expect(() => [.. c.Ops.Select(op => $"{Model(model, op, item)} #{model.Count}")], out _);

        var actual = new List<string>();
        var result = source.Mutate((c.Ops, item, actual), static (s, a) =>
        {
            foreach (var op in a.Ops) a.actual.Add($"{Apply(s, op, a.item)} #{s.Count}");
        });
        Assert.Equal(expected, actual);
        AssertEqual(model, result);
        AssertEqual(start, source);
        if (c.Same) Assert.True(result.IsSameAs(source), "a batch that changed nothing made a new version");
        if (model.Count == 0) Assert.True(result.IsSameAs(ImmSet<T>.Empty));
        _ = result.Mutate((model, item), static (s, a) =>
        {
            for (var i = 1000; i < 1100; i++) s.Add(a.item(i));
            foreach (var x in a.model) s.Remove(x);
        });
        AssertEqual(model, result);
    }

    private static string Apply<T>(ImmSet<T>.Scope s, Op op, Func<int, T> item) where T : notnull => op.Kind switch
    {
        Do.Has => Partition.Yes(s.Contains(item(op.A))),
        Do.Add => Partition.Yes(s.Add(item(op.A))),
        Do.Remove => Partition.Yes(s.Remove(item(op.A))),
        Do.Clear => Cleared(s),
        _ => throw new ArgumentException($"{op.Kind} is not a set operation"),
    };

    private static string Cleared<T>(ImmSet<T>.Scope s) where T : notnull { s.Clear(); return ""; }

    private static string Model<T>(HashSet<T> h, Op op, Func<int, T> item) where T : notnull
    {
        switch (op.Kind)
        {
            case Do.Has: return Partition.Yes(h.Contains(item(op.A)));
            case Do.Add: return Partition.Yes(h.Add(item(op.A)));
            case Do.Remove: return Partition.Yes(h.Remove(item(op.A)));
            case Do.Clear: h.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not a set operation");
        }
    }

    private static void AssertEqual<T>(HashSet<T> expected, ImmSet<T> actual) where T : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var x in expected) Assert.True(actual.Contains(x), $"missing {x}");
        Assert.True(expected.SetEquals(actual));
        Check.Set(actual);
    }
}

// ----------------------------------------------------------------------------------------------------- ImmOrderedMap

public class OrderedMapMutationPartitionTests
{
    private static Op Get(int k) => new(Do.Get, k);
    private static Op Item(int k) => new(Do.Item, k);
    private static Op Has(int k) => new(Do.Has, k);
    private static Op Put(int k, int v) => new(Do.Put, k, v);
    private static Op Bump(int k, int by) => new(Do.Bump, k, by);
    private static Op Remove(int k) => new(Do.Remove, k);
    private static readonly Op Clear = new(Do.Clear);
    private static readonly int[] Many = Partition.Many;
    private static readonly int[] Hundred = [.. Enumerable.Range(0, 100)];

    private static readonly Dictionary<string, KeyedCase> Cases = new()
    {
        ["create: absent key, empty map"] = new([], [Put(1, 7)]),
        ["create: absent key is appended"] = new(Many, [Put(99, 7)]),
        ["create: by an upsert is appended"] = new(Many, [Bump(99, 7)]),
        ["create: key present at the start updates it in place"] = new(Many, [Put(3, 7)]),
        ["create: key removed earlier in the batch is appended"] = new(Many, [Remove(3), Put(3, 7)]),
        ["create: after a clear"] = new(Many, [Clear, Put(3, 7), Put(1, 8)]),
        ["read: present at the start"] = new(Many, [Get(3), Item(3), Has(3)], Same: true),
        ["read: absent"] = new(Many, [Get(99), Has(99)], Same: true),
        ["read: empty map"] = new([], [Get(1), Has(1)], Same: true),
        ["read: created in the batch"] = new([], [Put(1, 5), Get(1), Item(1)]),
        ["read: removed in the batch"] = new(Many, [Remove(3), Get(3), Has(3)]),
        ["read: the indexer on an absent key throws"] = new(Many, [Put(3, 1), Item(99)]),
        ["update: present at the start keeps its place"] = new(Many, [Put(3, 99), Bump(0, 1), Bump(39, 2)]),
        ["update: created in the batch"] = new([], [Put(1, 1), Put(1, 2), Bump(1, 3)]),
        ["update: then removed"] = new(Many, [Put(3, 1), Remove(3)]),
        ["delete: the first"] = new(Many, [Remove(0)]),
        ["delete: the last"] = new(Many, [Remove(39)]),
        ["delete: in the middle"] = new(Many, [Remove(20)]),
        ["delete: the first, past interior holes"] = new(Many, [Remove(1), Remove(2), Remove(0)]),
        ["delete: the last, past interior holes"] = new(Many, [Remove(38), Remove(37), Remove(39)]),
        ["delete: the only key leaves the empty map"] = new([1], [Remove(1)]),
        ["delete: absent"] = new(Many, [Remove(99)], Same: true),
        ["delete: empty map"] = new([], [Remove(1)], Same: true),
        ["delete: twice"] = new(Many, [Remove(3), Remove(3)]),
        ["delete: created in the batch"] = new(Many, [Put(99, 1), Remove(99)]),
        ["delete: most of the interior compacts, order kept"] = new(Hundred, [.. Enumerable.Range(1, 60).Select(Remove), Put(500, 1), Put(30, 2)]),
        ["delete: every key"] = new(Many, [.. Many.Select(Remove)]),
        ["clear: populated"] = new(Many, [Clear]),
        ["clear: empty"] = new([], [Clear], Same: true),
        ["batch: no operations"] = new(Many, [], Same: true),
    };

    public static TheoryData<string> Partitions => Partition.Names(Cases);

    [Theory, MemberData(nameof(Partitions))] public void IntKeys(string partition) => Run(i => i, Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void StringKeys(string partition) => Run(i => $"k{i}", Cases[partition]);
    [Theory, MemberData(nameof(Partitions))] public void CollidingKeys(string partition) => Run(i => new BadKey(i), Cases[partition]);

    private static void Run<K>(Func<int, K> key, KeyedCase c) where K : notnull
    {
        var start = new OrderedDictionary<K, int>(c.Start.Select(i => KeyValuePair.Create(key(i), i * 10)));
        var source = ImmOrderedMap<K, int>.Empty.Mutate(start, static (s, d) => { foreach (var (k, v) in d) s[k] = v; });
        var model = new OrderedDictionary<K, int>(start);
        var expected = Partition.Expect(() =>
        {
            var log = new List<string>();
            foreach (var op in c.Ops) log.Add($"{Model(model, op, key)} #{model.Count}");
            return log;
        }, out var thrown);
        if (thrown is not null)
        {
            Assert.Throws(thrown.GetType(), () => source.Mutate(s => { foreach (var op in c.Ops) Apply(s, op, key); }));
            AssertEqual(start, source);
            Assert.True(source.Mutate(static _ => { }).IsSameAs(source));
            Assert.Single(ImmOrderedMap<K, int>.Empty.Mutate(s => s[key(1)] = 1));
            return;
        }

        var actual = new List<string>();
        var result = source.Mutate((c.Ops, key, actual), static (s, a) =>
        {
            foreach (var op in a.Ops) a.actual.Add($"{Apply(s, op, a.key)} #{s.Count}");
        });
        Assert.Equal(expected, actual);
        AssertEqual(model, result);
        AssertEqual(start, source);
        if (c.Same) Assert.True(result.IsSameAs(source), "a batch that changed nothing made a new version");
        if (model.Count == 0) Assert.True(result.IsSameAs(ImmOrderedMap<K, int>.Empty));
        _ = result.Mutate((model, key), static (s, a) =>
        {
            foreach (var k in a.model.Keys) s[k] = -1;
            for (var i = 1000; i < 1100; i++) s[a.key(i)] = i;
            foreach (var k in a.model.Keys) s.Remove(k);
        });
        AssertEqual(model, result);
    }

    private static string Apply<K>(ImmOrderedMap<K, int>.Scope s, Op op, Func<int, K> key) where K : notnull
    {
        var k = key(op.A);
        switch (op.Kind)
        {
            case Do.Get: return Partition.Found(s.TryGetValue(k, out var v), v);
            case Do.Item: return $"={s[k]}";
            case Do.Has: return Partition.Yes(s.ContainsKey(k));
            case Do.Put: s[k] = op.B; return "";
            case Do.Bump: s.GetValueRefOrAddDefault(k, out var existed) += op.B; return Partition.Yes(existed);
            case Do.Remove: return Partition.Yes(s.Remove(k));
            case Do.Clear: s.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not an ordered map operation");
        }
    }

    private static string Model<K>(OrderedDictionary<K, int> d, Op op, Func<int, K> key) where K : notnull
    {
        var k = key(op.A);
        switch (op.Kind)
        {
            case Do.Get: return Partition.Found(d.TryGetValue(k, out var v), v);
            case Do.Item: return d.TryGetValue(k, out var w) ? $"={w}" : throw new KeyNotFoundException();
            case Do.Has: return Partition.Yes(d.ContainsKey(k));
            case Do.Put: d[k] = op.B; return "";
            case Do.Bump: { var existed = d.TryGetValue(k, out var x); d[k] = x + op.B; return Partition.Yes(existed); }
            case Do.Remove: return Partition.Yes(d.Remove(k));
            case Do.Clear: d.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not an ordered map operation");
        }
    }

    private static void AssertEqual<K>(OrderedDictionary<K, int> expected, ImmOrderedMap<K, int> actual) where K : notnull
    {
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected, actual);                                 // insertion order included
        foreach (var (k, v) in expected) Assert.True(actual.TryGetValue(k, out var a) && a == v, $"{k}: expected {v}");
    }
}

// ------------------------------------------------------------------------------------------- ImmVector, ImmFlexVector

public record PositionalCase(int Start, Op[] Ops, bool Same = false);

/// <summary>The partitions <see cref="ImmVector{T}"/> and <see cref="ImmFlexVector{T}"/> share. Values start as their
/// index; shapes sit at the storage edges: empty, one, a full tail (32), one past it, a full two-level tree (1,056)
/// and one past it (a new level).</summary>
internal static class Positional
{
    public static Op Get(int i) => new(Do.Get, i);
    public static Op Set(int i, int v) => new(Do.Put, i, v);
    public static Op Bump(int i, int by) => new(Do.Bump, i, by);
    public static Op Add(int v) => new(Do.Add, v);
    public static Op AddRange(int from, int count) => new(Do.AddRange, from, count);
    public static Op Take(int n) => new(Do.Take, n);
    public static readonly Op Clear = new(Do.Clear);

    public static Dictionary<string, PositionalCase> Shared() => new()
    {
        ["create: append to empty"] = new(0, [Add(7)]),
        ["create: append within the tail"] = new(5, [Add(7)]),
        ["create: append fills the tail"] = new(31, [Add(7)]),
        ["create: append spills a full tail into the tree"] = new(32, [Add(7)]),
        ["create: append grows the tree a level"] = new(1056, [Add(7)]),
        ["create: append a range across leaves"] = new(30, [AddRange(100, 70)]),
        ["create: append an empty range"] = new(30, [AddRange(100, 0)]),
        ["create: append after a clear"] = new(40, [Clear, Add(7)]),
        ["read: first, last, in the tree and in the tail"] = new(1057, [Get(0), Get(1056), Get(500), Get(1040)], Same: true),
        ["read: appended in the batch"] = new(3, [Add(7), Get(3)]),
        ["read: index == Count throws"] = new(3, [Get(3)]),
        ["read: negative index throws"] = new(3, [Get(-1)]),
        ["read: empty throws"] = new(0, [Get(0)]),
        ["read: an element truncated in the batch throws"] = new(3, [Take(2), Get(2)]),
        ["update: in the tail"] = new(40, [Set(35, -1)]),
        ["update: in the tree"] = new(1057, [Set(5, -1), Set(1000, -2)]),
        ["update: by reference"] = new(40, [Bump(3, 5), Bump(39, 1)]),
        ["update: appended in the batch"] = new(3, [Add(7), Set(3, 8)]),
        ["update: index == Count throws"] = new(3, [Set(3, 1)]),
        ["update: by reference, out of range, throws"] = new(3, [Bump(5, 1)]),
        ["delete: take fewer, within the tail"] = new(40, [Take(35)]),
        ["delete: take fewer, into the tree"] = new(1057, [Take(100)]),
        ["delete: take fewer, into the old tail"] = new(1057, [Take(1050)]),
        ["delete: take all is a no-op"] = new(40, [Take(40)], Same: true),
        ["delete: take more than all is a no-op"] = new(40, [Take(100)], Same: true),
        ["delete: take zero empties"] = new(40, [Take(0)]),
        ["delete: take, negative, throws"] = new(40, [Take(-1)]),
        ["delete: take what the batch appended"] = new(40, [AddRange(100, 50), Take(40)]),
        ["clear: populated"] = new(1057, [Clear]),
        ["clear: empty"] = new(0, [Clear], Same: true),
        ["batch: no operations"] = new(1057, [], Same: true),
    };

    /// <summary>The model's answer for a step; throws the library's exceptions for the library's refusals.</summary>
    public static string Model(List<int> l, Op op)
    {
        switch (op.Kind)
        {
            case Do.Get: return $"={l[Index(l, op.A)]}";
            case Do.Put: l[Index(l, op.A)] = op.B; return "";
            case Do.Bump: l[Index(l, op.A)] += op.B; return "";
            case Do.Add: l.Add(op.A); return "";
            case Do.AddRange: case Do.AddFlex: l.AddRange(Enumerable.Range(op.A, op.B)); return "";
            case Do.Insert:
                if ((uint)op.A > (uint)l.Count) throw new ArgumentOutOfRangeException("index");
                l.Insert(op.A, op.B);
                return "";
            case Do.RemoveAt: case Do.RemoveRange:
                var count = op.Kind == Do.RemoveAt ? 1 : op.B;
                if ((uint)op.A > (uint)l.Count || (uint)count > (uint)(l.Count - op.A)) throw new ArgumentOutOfRangeException("index");
                l.RemoveRange(op.A, count);
                return "";
            case Do.RemoveLast:
                if (l.Count == 0) throw new InvalidOperationException();
                l.RemoveAt(l.Count - 1);
                return "";
            case Do.Take:
                ArgumentOutOfRangeException.ThrowIfNegative(op.A);
                if (op.A < l.Count) l.RemoveRange(op.A, l.Count - op.A);
                return "";
            case Do.Skip:
                ArgumentOutOfRangeException.ThrowIfNegative(op.A);
                l.RemoveRange(0, Math.Min(op.A, l.Count));
                return "";
            case Do.Clear: l.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not a vector operation");
        }
    }

    private static int Index(List<int> l, int i) => (uint)i < (uint)l.Count ? i : throw new ArgumentOutOfRangeException("index");
}

public class VectorMutationPartitionTests
{
    private static readonly Op RemoveLast = new(Do.RemoveLast);

    private static readonly Dictionary<string, PositionalCase> Cases = Extend(Positional.Shared());

    private static Dictionary<string, PositionalCase> Extend(Dictionary<string, PositionalCase> d)
    {
        d["delete: remove last, within the tail"] = new(40, [RemoveLast]);
        d["delete: remove last, the tail's only element"] = new(33, [RemoveLast]);
        d["delete: remove last, shrinks the tree a level"] = new(1057, [RemoveLast]);
        d["delete: remove last, the only element"] = new(1, [RemoveLast]);
        d["delete: remove last, empty, throws"] = new(0, [RemoveLast]);
        d["delete: remove last, appended in the batch"] = new(5, [Positional.Add(9), RemoveLast]);
        d["delete: remove last, down to empty and back"] = new(33, [.. Enumerable.Repeat(RemoveLast, 33), Positional.Add(1)]);
        return d;
    }

    public static TheoryData<string> Partitions => Partition.Names(Cases);

    [Theory, MemberData(nameof(Partitions))]
    public void Partitions_MatchTheModel(string partition)
    {
        var c = Cases[partition];
        var start = Enumerable.Range(0, c.Start).ToList();
        var source = ImmVector.Create(start.ToArray());
        var model = new List<int>(start);
        var expected = Partition.Expect(() => [.. c.Ops.Select(op => $"{Positional.Model(model, op)} #{model.Count}")], out var thrown);
        if (thrown is not null)
        {
            Assert.Throws(thrown.GetType(), () => source.Mutate(s => { foreach (var op in c.Ops) Apply(s, op); }));
            Assert.Equal(start, source);
            Assert.True(source.Mutate(static _ => { }).IsSameAs(source));
            Assert.Equal(new[] { 1 }, ImmVector<int>.Empty.Mutate(static s => s.Add(1)));
            return;
        }

        var actual = new List<string>();
        var result = source.Mutate((c.Ops, actual), static (s, a) =>
        {
            foreach (var op in a.Ops) a.actual.Add($"{Apply(s, op)} #{s.Count}");
        });
        Assert.Equal(expected, actual);
        Assert.Equal(model, result);
        Assert.Equal(model.Count, result.Count);
        Assert.Equal(start, source);
        if (c.Same) Assert.True(result.IsSameAs(source), "a batch that changed nothing made a new version");
        if (model.Count == 0) Assert.True(result.IsSameAs(ImmVector<int>.Empty));
        _ = result.Mutate(static s =>
        {
            for (var i = 0; i < s.Count; i++) s[i] = -1;
            for (var i = 0; i < 100; i++) s.Add(i);
            s.Take(1);
        });
        Assert.Equal(model, result);
    }

    private static string Apply(ImmVector<int>.Scope s, Op op)
    {
        switch (op.Kind)
        {
            case Do.Get: return $"={s[op.A]}";
            case Do.Put: s[op.A] = op.B; return "";
            case Do.Bump: s.ItemRef(op.A) += op.B; return "";
            case Do.Add: s.Add(op.A); return "";
            case Do.AddRange: s.AddRange(Enumerable.Range(op.A, op.B).ToArray()); return "";
            case Do.RemoveLast: s.RemoveLast(); return "";
            case Do.Take: s.Take(op.A); return "";
            case Do.Clear: s.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not an ImmVector operation");
        }
    }
}

public class FlexVectorMutationPartitionTests
{
    private static Op Insert(int i, int v) => new(Do.Insert, i, v);
    private static Op RemoveAt(int i) => new(Do.RemoveAt, i);
    private static Op RemoveRange(int i, int n) => new(Do.RemoveRange, i, n);
    private static Op Skip(int n) => new(Do.Skip, n);
    private static Op AddFlex(int from, int count) => new(Do.AddFlex, from, count);

    private static readonly Dictionary<string, PositionalCase> Cases = Extend(Positional.Shared());

    private static Dictionary<string, PositionalCase> Extend(Dictionary<string, PositionalCase> d)
    {
        d["create: insert at the front"] = new(100, [Insert(0, -1)]);
        d["create: insert in the middle"] = new(1057, [Insert(500, -1)]);
        d["create: insert at the end (index == Count)"] = new(100, [Insert(100, -1)]);
        d["create: insert into empty"] = new(0, [Insert(0, 1)]);
        d["create: insert past the end throws"] = new(100, [Insert(101, 1)]);
        d["create: insert, negative index, throws"] = new(100, [Insert(-1, 1)]);
        d["create: concatenate a vector"] = new(100, [AddFlex(1000, 77)]);
        d["create: concatenate onto empty"] = new(0, [AddFlex(1000, 77)]);
        d["create: concatenate an empty vector"] = new(100, [AddFlex(1000, 0)], Same: true);
        d["read: an inserted element"] = new(100, [Insert(50, -1), Positional.Get(50), Positional.Get(51)]);
        d["update: an inserted element"] = new(100, [Insert(50, -1), Positional.Set(50, -2)]);
        d["delete: remove at the front"] = new(100, [RemoveAt(0)]);
        d["delete: remove in the middle"] = new(1057, [RemoveAt(500)]);
        d["delete: remove the last"] = new(100, [RemoveAt(99)]);
        d["delete: remove the only element"] = new(1, [RemoveAt(0)]);
        d["delete: remove at index == Count throws"] = new(100, [RemoveAt(100)]);
        d["delete: remove at, empty, throws"] = new(0, [RemoveAt(0)]);
        d["delete: remove what the batch inserted"] = new(100, [Insert(30, -1), RemoveAt(30)]);
        d["delete: remove a range across leaves"] = new(1057, [RemoveRange(20, 900)]);
        d["delete: remove a range of everything"] = new(100, [RemoveRange(0, 100)]);
        d["delete: remove an empty range at the end is a no-op"] = new(100, [RemoveRange(100, 0)], Same: true);
        d["delete: remove a range past the end throws"] = new(100, [RemoveRange(90, 11)]);
        d["delete: skip some"] = new(1057, [Skip(40)]);
        d["delete: skip all"] = new(100, [Skip(100)]);
        d["delete: skip more than all"] = new(100, [Skip(1000)]);
        d["delete: skip zero is a no-op"] = new(100, [Skip(0)], Same: true);
        d["delete: skip, negative, throws"] = new(100, [Skip(-1)]);
        d["delete: skip and take to a middle slice"] = new(1057, [Skip(100), Positional.Take(500)]);
        return d;
    }

    public static TheoryData<string, bool> Partitions
    {
        get
        {
            var d = new TheoryData<string, bool>();
            foreach (var name in Cases.Keys) { d.Add(name, false); d.Add(name, true); }
            return d;
        }
    }

    /// <summary>A start of <paramref name="n"/> elements, dense or relaxed (concatenated from uneven pieces).</summary>
    private static ImmFlexVector<int> Start(int n, bool relaxed)
    {
        if (!relaxed) return ImmFlexVector.Create(Enumerable.Range(0, n).ToArray());
        var rnd = new Random(n);
        var v = ImmFlexVector<int>.Empty;
        for (var i = 0; i < n;)
        {
            var k = Math.Min(n - i, 1 + rnd.Next(70));
            v += ImmFlexVector.Create(Enumerable.Range(i, k).ToArray());
            i += k;
        }
        return v;
    }

    [Theory, MemberData(nameof(Partitions))]
    public void Partitions_MatchTheModel(string partition, bool relaxed)
    {
        var c = Cases[partition];
        var source = Start(c.Start, relaxed);
        var start = Enumerable.Range(0, c.Start).ToList();
        Assert.Equal(start, source);
        var model = new List<int>(start);
        var expected = Partition.Expect(() => [.. c.Ops.Select(op => $"{Positional.Model(model, op)} #{model.Count}")], out var thrown);
        if (thrown is not null)
        {
            Assert.Throws(thrown.GetType(), () => source.Mutate(s => { foreach (var op in c.Ops) Apply(s, op); }));
            Assert.Equal(start, source);
            Assert.True(source.Mutate(static _ => { }).IsSameAs(source));
            Assert.Equal(new[] { 1 }, ImmFlexVector<int>.Empty.Mutate(static s => s.Add(1)));
            return;
        }

        var actual = new List<string>();
        var result = source.Mutate((c.Ops, actual), static (s, a) =>
        {
            foreach (var op in a.Ops) a.actual.Add($"{Apply(s, op)} #{s.Count}");
        });
        Assert.Equal(expected, actual);
        Assert.Equal(model, result);
        Assert.Equal(model.Count, result.Count);
        Assert.Equal(start, source);
        Check.FlexVector(result);
        if (c.Same) Assert.True(result.IsSameAs(source), "a batch that changed nothing made a new version");
        if (model.Count == 0) Assert.True(result.IsSameAs(ImmFlexVector<int>.Empty));
        _ = result.Mutate(static s =>
        {
            for (var i = 0; i < s.Count; i++) s[i] = -1;
            for (var i = 0; i < 100; i++) s.Insert(s.Count / 2, i);
            s.RemoveRange(0, s.Count / 2);
        });
        Assert.Equal(model, result);
    }

    private static string Apply(ImmFlexVector<int>.Scope s, Op op)
    {
        switch (op.Kind)
        {
            case Do.Get: return $"={s[op.A]}";
            case Do.Put: s[op.A] = op.B; return "";
            case Do.Bump: s.ItemRef(op.A) += op.B; return "";
            case Do.Add: s.Add(op.A); return "";
            case Do.AddRange: s.AddRange(Enumerable.Range(op.A, op.B).ToArray()); return "";
            case Do.AddFlex: s.AddRange(ImmFlexVector.Create(Enumerable.Range(op.A, op.B).ToArray())); return "";
            case Do.Insert: s.Insert(op.A, op.B); return "";
            case Do.RemoveAt: s.RemoveAt(op.A); return "";
            case Do.RemoveRange: s.RemoveRange(op.A, op.B); return "";
            case Do.Take: s.Take(op.A); return "";
            case Do.Skip: s.Skip(op.A); return "";
            case Do.Clear: s.Clear(); return "";
            default: throw new ArgumentException($"{op.Kind} is not an ImmFlexVector operation");
        }
    }
}

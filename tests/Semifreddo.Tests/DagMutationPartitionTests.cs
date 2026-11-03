using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>
/// CRUD partitions for <see cref="ImmDag{N, A, V}"/> batches, each run three ways: <c>Mutate</c>, <c>TryMutate</c> and
/// <c>TryMutate</c> with an order. A BCL model of the documented semantics gives each step's answer, the counts after
/// it, the resulting graph, and whether the batch leaves a cycle (a refusal: the source, and the order, unchanged),
/// or throws. The start is a small graph (1→2→3, 1→4→5, 6 alone) or, for the hub shape, the same with node 1 given
/// 34 more successors, so its edge set is a CHAMP rather than inline.
/// </summary>
public class DagMutationPartitionTests
{
    private static Op AddNode(int id, int alt, int value = 0) => new(Do.AddNode, id, alt, value);
    private static Op RemoveNode(int id) => new(Do.RemoveNode, id);
    private static Op SetValue(int id, int value) => new(Do.SetValue, id, value);
    private static Op Rename(int oldId, int newId) => new(Do.Rename, oldId, newId);
    private static Op Replace(int oldId, int newId, int alt, int value) => new(Do.Replace, oldId, newId, alt, value);
    private static Op AddEdge(int from, int to) => new(Do.AddEdge, from, to);
    private static Op RemoveEdge(int from, int to) => new(Do.RemoveEdge, from, to);
    private static Op Has(int id) => new(Do.Has, id);
    private static Op Value(int id) => new(Do.Get, id);
    private static Op Alt(int id) => new(Do.Alt, id);
    private static Op ByAlt(int alt) => new(Do.ByAlt, alt);
    private static Op HasEdge(int from, int to) => new(Do.HasEdge, from, to);

    public record DagCase(Op[] Ops, bool Same = false, bool FromEmpty = false);

    private static readonly Dictionary<string, DagCase> Cases = new()
    {
        ["node create: absent id and alternate key"] = new([AddNode(100, 900, 1)]),
        ["node create: into the empty graph"] = new([AddNode(1, 1, 1), AddNode(2, 2, 2)], FromEmpty: true),
        ["node create: id present at the start is refused"] = new([AddNode(2, 900)], Same: true),
        ["node create: alternate key present at the start is refused"] = new([AddNode(100, 102)], Same: true),
        ["node create: id created earlier in the batch is refused"] = new([AddNode(100, 900, 1), AddNode(100, 901, 2)]),
        ["node create: alternate key created earlier in the batch is refused"] = new([AddNode(100, 900, 1), AddNode(101, 900, 2)]),
        ["node create: removed earlier in the batch, added again"] = new([RemoveNode(3), AddNode(3, 103, 5)]),
        ["node create: alternate key freed by a removal"] = new([RemoveNode(3), AddNode(100, 103, 5)]),
        ["node create: alternate key freed by a re-key"] = new([Replace(3, 3, 903, 30), AddNode(100, 103, 5)]),
        ["node create: id freed by a rename"] = new([Rename(3, 100), AddNode(3, 903, 5)]),

        ["node read: present at the start"] = new([Has(2), Value(2), Alt(2), ByAlt(102)], Same: true),
        ["node read: absent"] = new([Has(100), Value(100), Alt(100), ByAlt(900)], Same: true),
        ["node read: the empty graph"] = new([Has(1), Value(1), ByAlt(1)], Same: true, FromEmpty: true),
        ["node read: created in the batch"] = new([AddNode(100, 900, 1), Has(100), Value(100), Alt(100), ByAlt(900)]),
        ["node read: removed in the batch"] = new([RemoveNode(2), Has(2), Value(2), Alt(2), ByAlt(102)]),
        ["node read: renamed in the batch"] = new([Rename(2, 100), Has(2), Has(100), ByAlt(102), HasEdge(1, 100), HasEdge(100, 3)]),

        ["node update: value, present at the start"] = new([SetValue(2, 99)]),
        ["node update: value, absent, is refused"] = new([SetValue(100, 99)], Same: true),
        ["node update: value, created in the batch"] = new([AddNode(100, 900, 1), SetValue(100, 2)]),
        ["node update: value, removed in the batch, is refused"] = new([RemoveNode(2), SetValue(2, 1)]),
        ["node update: rename to a free id keeps the edges"] = new([Rename(2, 100)]),
        ["node update: rename to a taken id is refused"] = new([Rename(2, 3)], Same: true),
        ["node update: rename an absent node is refused"] = new([Rename(100, 101)], Same: true),
        ["node update: rename with a taken alternate key is refused"] = new([Replace(2, 100, 103, 0)], Same: true),
        ["node update: re-key under the same id"] = new([Replace(2, 2, 902, 20), ByAlt(102), ByAlt(902)]),
        ["node update: same id and key, new value"] = new([Replace(2, 2, 102, 99)]),
        ["node update: rename and back"] = new([Rename(2, 100), Rename(100, 2)]),
        ["node update: rename a node created in the batch"] = new([AddNode(100, 900, 1), AddEdge(100, 2), Rename(100, 101)]),
        ["node update: rename onto an id removed in the batch"] = new([RemoveNode(6), Rename(2, 6)]),
        ["node update: rename the hub"] = new([Rename(1, 100)]),
        ["node update: rename, then remove"] = new([Rename(2, 100), RemoveNode(100)]),

        ["node delete: no edges"] = new([RemoveNode(6)]),
        ["node delete: in and out edges go with it"] = new([RemoveNode(2)]),
        ["node delete: the hub"] = new([RemoveNode(1)]),
        ["node delete: absent is refused"] = new([RemoveNode(100)], Same: true),
        ["node delete: the empty graph"] = new([RemoveNode(1)], Same: true, FromEmpty: true),
        ["node delete: twice"] = new([RemoveNode(2), RemoveNode(2)]),
        ["node delete: created in the batch"] = new([AddNode(100, 900, 1), AddEdge(1, 100), RemoveNode(100)]),
        ["node delete: every node"] = new([.. Enumerable.Range(1, 40).Select(RemoveNode)]),

        ["edge create: both ends present"] = new([AddEdge(3, 6)]),
        ["edge create: already present is refused"] = new([AddEdge(1, 2)], Same: true),
        ["edge create: from an absent node throws"] = new([AddEdge(100, 2)]),
        ["edge create: to an absent node throws"] = new([AddEdge(2, 100)]),
        ["edge create: to a node removed in the batch throws"] = new([RemoveNode(6), AddEdge(2, 6)]),
        ["edge create: to a node created in the batch"] = new([AddNode(100, 900), AddEdge(3, 100)]),
        ["edge create: between nodes created in the batch"] = new([AddNode(100, 900), AddNode(101, 901), AddEdge(100, 101)]),
        ["edge create: removed earlier in the batch"] = new([RemoveEdge(1, 2), AddEdge(1, 2)]),
        ["edge create: a self-loop is a cycle"] = new([AddEdge(2, 2)]),
        ["edge create: a self-loop on an absent node throws"] = new([AddEdge(100, 100)]),
        ["edge create: the reverse of an edge is a cycle"] = new([AddEdge(2, 1)]),
        ["edge create: closing a longer cycle"] = new([AddEdge(3, 1)]),
        ["edge create: a cycle broken later in the batch"] = new([AddEdge(3, 1), RemoveEdge(1, 2)]),
        ["edge create: against the insertion order"] = new([AddEdge(6, 1), AddEdge(5, 6)]),

        ["edge read: present, absent, reversed"] = new([HasEdge(1, 2), HasEdge(3, 6), HasEdge(2, 1), HasEdge(100, 1)], Same: true),
        ["edge read: created and removed in the batch"] = new([AddEdge(3, 6), HasEdge(3, 6), RemoveEdge(1, 2), HasEdge(1, 2)]),

        ["edge delete: present"] = new([RemoveEdge(1, 2)]),
        ["edge delete: from the hub"] = new([RemoveEdge(1, 4)]),
        ["edge delete: absent is refused"] = new([RemoveEdge(3, 6)], Same: true),
        ["edge delete: between absent nodes is refused"] = new([RemoveEdge(100, 101)], Same: true),
        ["edge delete: created in the batch"] = new([AddEdge(3, 6), RemoveEdge(3, 6)]),
        ["edge delete: twice"] = new([RemoveEdge(1, 2), RemoveEdge(1, 2)]),

        ["batch: no operations"] = new([], Same: true),
        ["batch: every operation"] = new([AddNode(100, 900, 1), AddEdge(3, 100), SetValue(2, 7), Rename(4, 101),
            RemoveEdge(1, 2), RemoveNode(6), Replace(5, 5, 905, 50), AddEdge(100, 101)]),
    };

    public static TheoryData<string, bool> Partitions
    {
        get
        {
            var d = new TheoryData<string, bool>();
            foreach (var (name, c) in Cases)
            {
                d.Add(name, false);
                if (!c.FromEmpty) d.Add(name, true);
            }
            return d;
        }
    }

    private sealed class Model
    {
        public readonly Dictionary<int, (int Alt, int Value)> Nodes = [];
        public readonly HashSet<(int From, int To)> Edges = [];
        public bool SelfLoop;

        public Model Clone()
        {
            var m = new Model { SelfLoop = SelfLoop };
            foreach (var (k, v) in Nodes) m.Nodes[k] = v;
            m.Edges.UnionWith(Edges);
            return m;
        }

        public bool AltTaken(int alt) => Nodes.Values.Any(n => n.Alt == alt);

        public bool IsAcyclic()
        {
            if (SelfLoop) return false;
            var indeg = Nodes.Keys.ToDictionary(k => k, k => Edges.Count(e => e.To == k));
            var q = new Queue<int>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var seen = 0;
            while (q.Count > 0)
            {
                var n = q.Dequeue();
                seen++;
                foreach (var (_, to) in Edges.Where(e => e.From == n)) if (--indeg[to] == 0) q.Enqueue(to);
            }
            return seen == Nodes.Count;
        }

        public string Step(Op op)
        {
            switch (op.Kind)
            {
                case Do.AddNode:
                    if (Nodes.ContainsKey(op.A) || AltTaken(op.B)) return "false";
                    Nodes[op.A] = (op.B, op.C);
                    return "true";
                case Do.RemoveNode:
                    if (!Nodes.Remove(op.A)) return "false";
                    Edges.RemoveWhere(e => e.From == op.A || e.To == op.A);
                    return "true";
                case Do.SetValue:
                    if (!Nodes.TryGetValue(op.A, out var n)) return "false";
                    Nodes[op.A] = (n.Alt, op.B);
                    return "true";
                case Do.Rename:
                    return Nodes.TryGetValue(op.A, out var r) ? Step(new Op(Do.Replace, op.A, op.B, r.Alt, r.Value)) : "false";
                case Do.Replace:
                    if (!Nodes.TryGetValue(op.A, out var old)) return "false";
                    if (op.B != op.A && Nodes.ContainsKey(op.B)) return "false";
                    if (op.C != old.Alt && AltTaken(op.C)) return "false";
                    Nodes.Remove(op.A);
                    Nodes[op.B] = (op.C, op.D);
                    var moved = Edges.Where(e => e.From == op.A || e.To == op.A).ToArray();
                    Edges.ExceptWith(moved);
                    foreach (var (f, t) in moved) Edges.Add((f == op.A ? op.B : f, t == op.A ? op.B : t));
                    return "true";
                case Do.AddEdge:
                    if (!Nodes.ContainsKey(op.A) || !Nodes.ContainsKey(op.B)) throw new KeyNotFoundException();
                    if (op.A == op.B) { SelfLoop = true; return "true"; }   // reported at the end, never stored
                    return Partition.Yes(Edges.Add((op.A, op.B)));
                case Do.RemoveEdge: return Partition.Yes(Edges.Remove((op.A, op.B)));
                case Do.Has: return Partition.Yes(Nodes.ContainsKey(op.A));
                case Do.Get: return Partition.Found(Nodes.TryGetValue(op.A, out var g), g.Value);
                case Do.Alt: return Partition.Found(Nodes.TryGetValue(op.A, out var a), a.Alt);
                case Do.ByAlt:
                    var hit = Nodes.Where(kv => kv.Value.Alt == op.A).Select(kv => kv.Key).ToArray();
                    return Partition.Found(hit.Length == 1, hit.FirstOrDefault());
                case Do.HasEdge: return Partition.Yes(Edges.Contains((op.A, op.B)));
                default: throw new ArgumentException($"{op.Kind} is not a DAG operation");
            }
        }
    }

    private static string Apply(ImmDag<int, int, int>.Scope s, Op op)
    {
        switch (op.Kind)
        {
            case Do.AddNode: return Partition.Yes(s.AddNode(op.A, op.B, op.C));
            case Do.RemoveNode: return Partition.Yes(s.RemoveNode(op.A));
            case Do.SetValue: return Partition.Yes(s.SetValue(op.A, op.B));
            case Do.Rename: return Partition.Yes(s.ReplaceNode(op.A, op.B));
            case Do.Replace: return Partition.Yes(s.ReplaceNode(op.A, op.B, op.C, op.D));
            case Do.AddEdge: return Partition.Yes(s.AddEdge(op.A, op.B));
            case Do.RemoveEdge: return Partition.Yes(s.RemoveEdge(op.A, op.B));
            case Do.Has: return Partition.Yes(s.ContainsNode(op.A));
            case Do.Get: return Partition.Found(s.TryGetValue(op.A, out var v), v);
            case Do.Alt: return Partition.Found(s.TryGetAlternateKey(op.A, out var a), a);
            case Do.ByAlt: return Partition.Found(s.TryGetNodeByAlternateKey(op.A, out var id), id);
            case Do.HasEdge: return Partition.Yes(s.ContainsEdge(op.A, op.B));
            default: throw new ArgumentException($"{op.Kind} is not a DAG operation");
        }
    }

    private static Model StartModel(bool hub, bool empty)
    {
        var m = new Model();
        if (empty) return m;
        for (var i = 1; i <= (hub ? 40 : 6); i++) m.Nodes[i] = (100 + i, 10 * i);
        m.Edges.UnionWith([(1, 2), (2, 3), (1, 4), (4, 5)]);
        if (hub) for (var i = 7; i <= 40; i++) m.Edges.Add((1, i));
        return m;
    }

    private static void Batch(ImmDag<int, int, int>.Scope s, (Op[] Ops, List<string> Log) a)
    {
        foreach (var op in a.Ops) a.Log.Add($"{Apply(s, op)} #{s.NodeCount}/{s.EdgeCount}");
    }

    [Theory, MemberData(nameof(Partitions))]
    public void Partitions_MatchTheModel(string partition, bool hub)
    {
        var c = Cases[partition];
        var startModel = StartModel(hub, c.FromEmpty);
        var order = IntOrderOnly.Empty;
        Assert.True(ImmDag<int, int, int>.Empty.TryMutate(ref order, startModel, static (s, m) =>
        {
            foreach (var (id, n) in m.Nodes) s.AddNode(id, n.Alt, n.Value);
            foreach (var (f, t) in m.Edges) s.AddEdge(f, t);
        }, out var source, out _));
        AssertEqual(startModel, source);

        var model = startModel.Clone();
        var expected = Partition.Expect(() =>
        {
            var log = new List<string>();
            foreach (var op in c.Ops) log.Add($"{model.Step(op)} #{model.Nodes.Count}/{model.Edges.Count}");
            return log;
        }, out var thrown);

        if (thrown is not null)
        {
            Assert.Throws(thrown.GetType(), () => source.Mutate(s => Batch(s, (c.Ops, []))));
            Assert.Throws(thrown.GetType(), () => source.TryMutate((c.Ops, new List<string>()), Batch, out _, out _));
            var o = order;
            Assert.Throws(thrown.GetType(), () => source.TryMutate(ref o, (c.Ops, new List<string>()), Batch, out _, out _));
            Assert.True(o.IsSameAs(order));
            AssertEqual(startModel, source);
            Assert.True(source.Mutate(static _ => { }).IsSameAs(source));
            return;
        }

        var acyclic = model.IsAcyclic();
        if (!acyclic) Assert.Throws<DagCycleException<int>>(() => source.Mutate(s => Batch(s, (c.Ops, []))));
        else Accepted(model, source.Mutate(s => Batch(s, (c.Ops, []))), source, c.Same, "Mutate");

        var plain = new List<string>();
        var ok = source.TryMutate((c.Ops, plain), Batch, out var result, out var cycle);
        Assert.Equal(expected, plain);
        Assert.Equal(acyclic, ok);
        if (ok) Accepted(model, result, source, c.Same, "TryMutate");
        else Refused(model, source, result, cycle!);

        var ordered = new List<string>();
        var next = order;
        ok = source.TryMutate(ref next, (c.Ops, ordered), Batch, out result, out cycle);
        Assert.Equal(expected, ordered);
        Assert.Equal(acyclic, ok);
        if (ok)
        {
            Accepted(model, result, source, c.Same, "TryMutate with an order");
            Check.TopologicalOrder(result, model.Nodes.Keys, next);
            if (c.Same) Assert.True(next.IsSameAs(order), "a batch that changed nothing moved the order");
        }
        else
        {
            Refused(model, source, result, cycle!);
            Assert.True(next.IsSameAs(order), "a refused batch moved the order");
        }
        AssertEqual(startModel, source);
    }

    private static void Accepted(Model model, ImmDag<int, int, int> result, ImmDag<int, int, int> source, bool same, string path)
    {
        AssertEqual(model, result);
        if (same) Assert.True(result.IsSameAs(source), $"{path}: a batch that changed nothing made a new version");
        _ = result.Mutate(model, static (s, m) => { foreach (var id in m.Nodes.Keys) s.RemoveNode(id); });
        AssertEqual(model, result);
    }

    /// <summary>A refusal returns the source and a cycle (first == last) whose every step is an edge of the batch's
    /// graph, or its self-loop.</summary>
    private static void Refused(Model model, ImmDag<int, int, int> source, ImmDag<int, int, int> result, int[] cycle)
    {
        Assert.True(result.IsSameAs(source), "a refused batch returned a new version");
        Assert.True(cycle.Length >= 2 && cycle[0] == cycle[^1], $"not a cycle: {string.Join(" -> ", cycle)}");
        for (var i = 1; i < cycle.Length; i++)
            Assert.True(model.Edges.Contains((cycle[i - 1], cycle[i])) || (model.SelfLoop && cycle[i - 1] == cycle[i]),
                $"{cycle[i - 1]} -> {cycle[i]} is not an edge of the batch");
    }

    private static void AssertEqual(Model m, ImmDag<int, int, int> d)
    {
        Assert.Equal(m.Nodes.Count, d.NodeCount);
        Assert.Equal(m.Edges.Count, d.EdgeCount);
        foreach (var v in d)
        {
            var (alt, value) = m.Nodes[v.Id];
            Assert.Equal(alt, v.AlternateKey);
            Assert.Equal(value, v.Value);
            Assert.True(d.TryGetNodeByAlternateKey(alt, out var id) && id == v.Id);
            Assert.True(m.Edges.Where(e => e.From == v.Id).Select(e => e.To).ToHashSet().SetEquals(v.Successors));
            Assert.True(m.Edges.Where(e => e.To == v.Id).Select(e => e.From).ToHashSet().SetEquals(v.Predecessors));
        }
        Check.Dag(d);
    }
}

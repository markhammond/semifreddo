using Xunit;

namespace Semifreddo.Reclamation.Tests;

public class RetiringDagTests
{
    private sealed class Payload(string name) { public string Name = name; public override string ToString() => Name; }

    private static RetiringDag<int, string, Payload> Seed(params Payload[] ps)
        => RetiringDag<int, string, Payload>.Empty.Mutate(ps, static (s, p) => { for (var i = 0; i < p.Length; i++) s.AddNode(i, "k" + i, p[i]); for (var i = 0; i + 1 < p.Length; i++) s.AddEdge(i, i + 1); });

    private static Payload[] Pending(RetiringDag<int, string, Payload> d) => [.. d.Retired.Select(r => r.Value)];

    private static RetiredNode<int, string, Payload>[] Candidates(RetiringDag<int, string, Payload> d)
    {
        var buf = new RetiredNode<int, string, Payload>[d.RetiredCount];
        return buf[..d.CopyRetiredTo(buf)];
    }

    [Fact]
    public void Remove_Retires_WithOriginPayload_AndOnlyThatPayloadComingBackDropsIt()
    {
        var p = new[] { new Payload("a"), new Payload("b"), new Payload("c") };
        var d = Seed(p);
        var removed = d.Mutate(static s => { s.SetValue(1, new Payload("changed-in-batch")); s.RemoveNode(1); });
        Assert.False(removed.Graph.ContainsNode(1));
        Assert.Equal(0, removed.Graph.EdgeCount);
        var r = Assert.Single(removed.Retired);
        Assert.Same(p[1], r.Value);              // the published payload, not the in-batch one
        Assert.Equal((1, "k1"), (r.Id, r.AlternateKey));
        Assert.True(removed.TryGetRetired(r.Stamp, out var same) && same == r);
        Assert.False(d.HasRetired);              // earlier versions keep their own ledger

        Assert.Same(p[1], Assert.Single(Pending(removed.AddNode(1, "k1", new Payload("b2")))));   // b is still gone
        Assert.False(removed.AddNode(1, "k1", p[1]).HasRetired);                                  // b is back: live again
        Assert.False(removed.AddNode(7, "k7", p[1]).HasRetired);                                  // under any id
    }

    [Fact]
    public void Reclaim_ByStamp_ReportsWhatItReclaimed()
    {
        var p = Enumerable.Range(0, 4).Select(i => new Payload("p" + i)).ToArray();
        var d = Seed(p).Mutate(static s => { s.RemoveNode(0); s.RemoveNode(1); s.RemoveNode(2); });
        var candidates = Candidates(d);                             // reclaimer's snapshot ...
        var again = new Payload("again");
        d = d.AddNode(1, "k1", again).RemoveNode(1);               // ... meanwhile id 1 comes back with a new payload, and goes

        var reclaimed = new List<RetiredNode<int, string, Payload>>();
        Assert.True(RetiringDag.Reclaim(ref d, candidates, reclaimed));
        Assert.Equal([p[0], p[1], p[2]], reclaimed.OrderBy(x => x.Id).Select(x => x.Value));
        Assert.Same(again, Assert.Single(Pending(d)));
        Assert.False(RetiringDag.Reclaim(ref d, candidates, reclaimed));   // reclaimed already: nothing, reported empty
        Assert.Empty(reclaimed);
        Assert.False(d.ReclaimAll().HasRetired);
    }

    [Fact]
    public void SamePayloadBackThenRemovedAgain_AStaleCandidateIsNotReclaimed()
    {
        // V1 retires b; a reclaimer reads it; V2 puts b back (an undo) and a reader takes V2; V3 removes b again. The
        // reclaimer waited only for holders older than V1: b, seen at V2, must not be reported reclaimed.
        var b = new Payload("b");
        var v1 = Seed(new Payload("a"), b).RemoveNode(1);
        var candidates = Candidates(v1);
        var v2 = v1.AddNode(1, "k1", b);
        var v3 = v2.RemoveNode(1);
        var shared = v3;
        var reclaimed = new List<RetiredNode<int, string, Payload>>();
        Assert.False(RetiringDag.Reclaim(ref shared, candidates, reclaimed));
        Assert.Empty(reclaimed);
        Assert.Same(b, Assert.Single(Pending(shared)));           // retired again, under a new stamp
        Assert.NotEqual(candidates[0].Stamp, shared.Retired.Single().Stamp);
    }

    [Fact]
    public void PayloadsReplacedUnderTheSameId_AreRetired()
    {
        var a = new Payload("a");
        var b = new Payload("b");
        var d = Seed(a, b);

        // one batch removes 1's node and renames 0 onto its id: b leaves, a only moves
        var renamedOnto = d.Mutate(static s => { s.RemoveNode(1); s.ReplaceNode(0, 1); });
        Assert.Same(b, Assert.Single(Pending(renamedOnto)));

        // removed and added again in one batch, with a new payload
        Assert.Same(b, Assert.Single(Pending(d.Mutate(static s => { s.RemoveNode(1); s.AddNode(1, "k1", new Payload("b2")); }))));
        // ... and with the same one: nothing left the graph
        Assert.False(d.Mutate(b, static (s, p) => { s.RemoveNode(1); s.AddNode(1, "k1", p); }).HasRetired);

        // a new payload by SetValue or ReplaceNode
        Assert.Same(b, Assert.Single(Pending(d.Mutate(static s => s.SetValue(1, new Payload("b3"))))));
        Assert.Same(b, Assert.Single(Pending(d.Mutate(static s => s.ReplaceNode(1, 1, "k1", new Payload("b4"))))));

        // two payloads swapped between nodes: both still held
        Assert.False(d.Mutate((a, b), static (s, p) => { s.SetValue(0, p.b); s.SetValue(1, p.a); }).HasRetired);
    }

    [Fact]
    public void Renames_AreNotRetired_RenameThenRemove_RetiresTheOriginal()
    {
        var p = new[] { new Payload("a"), new Payload("b"), new Payload("c") };
        var d = Seed(p);
        var renamed = d.Mutate(static s => { s.ReplaceNode(1, 10); s.ReplaceNode(10, 11); });
        Assert.False(renamed.HasRetired);
        Assert.True(renamed.Graph.ContainsEdge(0, 11) && renamed.Graph.ContainsEdge(11, 2));

        var gone = d.Mutate(static s => { s.ReplaceNode(1, 10); s.RemoveNode(10); });
        var r = Assert.Single(gone.Retired);
        Assert.Same(p[1], r.Value);
        Assert.Equal(1, r.Id);                   // the id it had where readers could see it
    }

    [Fact]
    public void AddedAndRemovedInOneBatch_IsNeverRetired()
    {
        var d = Seed(new Payload("a")).Mutate(static s => { s.AddNode(5, "k5", new Payload("tmp")); s.AddEdge(0, 5); s.RemoveNode(5); });
        Assert.False(d.HasRetired);
    }

    [Fact]
    public void CycleFailure_LeavesGraphAndLedgerUntouched()
    {
        var d = Seed(new Payload("a"), new Payload("b"), new Payload("c")).RemoveNode(2);
        Assert.False(d.TryMutate(0, static (s, _) => { s.RemoveNode(0); s.AddNode(9, "k9", new Payload("x")); s.AddEdge(1, 9); s.AddEdge(9, 1); }, out var same, out var cycle));
        Assert.True(same.IsSameAs(d));
        Assert.Equal(cycle[0], cycle[^1]);
        Assert.Equal(1, d.RetiredCount);
        Assert.Throws<DagCycleException<int>>(() => d.Mutate(static s => s.AddEdge(1, 1)));
    }

    [Fact]
    public void ConcurrentRemovals_ViaCas_RetireEverything()
    {
        var shared = RetiringDag<int, string, Payload>.Empty.Mutate(static s => { for (var i = 0; i < 400; i++) s.AddNode(i, "k" + i, new Payload("p" + i)); });
        Parallel.For(0, 8, w =>
        {
            for (var i = w; i < 400; i += 8) RetiringDag.Update(ref shared, static (d, id) => d.RemoveNode(id), i);
        });
        Assert.True(shared.Graph.IsEmpty);
        Assert.Equal(400, shared.RetiredCount);
    }

    private const int Ids = 12;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomHistories_NeverReclaimWhatAReaderCanSee_AndNeverLoseAPayload(int seed)
    {
        var rnd = new Random(seed);
        var d = RetiringDag<int, string, Payload>.Empty;
        var versions = new List<RetiringDag<int, string, Payload>> { d };
        var published = new HashSet<Payload>(ReferenceEqualityComparer.Instance);
        var released = new HashSet<Payload>(ReferenceEqualityComparer.Instance);
        var gone = new List<Payload>();                    // left the graph, not released: may come back (an undo)
        var fresh = 0;

        for (var step = 0; step < 3000; step++)
        {
            if (rnd.Next(10) < 8)
            {
                var plan = new List<(int Kind, int A, int B, Payload P)>();
                for (var e = rnd.Next(1, 4); e > 0; e--)
                {
                    var p = rnd.Next(3) == 0 && gone.Count > 0 ? gone[rnd.Next(gone.Count)] : new Payload("p" + fresh++);
                    plan.Add((rnd.Next(5), rnd.Next(Ids), rnd.Next(Ids), p));
                }
                // (renames reorder ids, so an edge may close a cycle: that batch changes nothing)
                if (d.TryMutate(plan, static (s, plan) =>
                {
                    foreach (var (kind, a, b, p) in plan)
                    {
                        switch (kind)
                        {
                            case 0: if (!s.ContainsNode(a) && !s.TryGetNodeByAlternateKey("k" + a, out _) && !Holds(s, p)) s.AddNode(a, "k" + a, p); break;
                            case 1: s.RemoveNode(a); break;
                            case 2: if (s.ContainsNode(a) && !Holds(s, p)) s.SetValue(a, p); break;
                            case 3: if (s.ContainsNode(a) && !s.ContainsNode(b)) s.ReplaceNode(a, b); break;
                            default: if (s.ContainsNode(a) && s.ContainsNode(b) && a < b) s.AddEdge(a, b); break;
                        }
                    }
                }, out var next, out _)) d = next;
            }
            else
            {
                // a reclaimer: candidates read at some past version Vk, every holder older than Vk done
                var k = rnd.Next(versions.Count);
                var candidates = versions[k].Retired.ToArray();
                var reclaimed = new List<RetiredNode<int, string, Payload>>();
                RetiringDag.Reclaim(ref d, candidates, reclaimed);
                foreach (var r in reclaimed)
                {
                    for (var j = k; j < versions.Count; j++)
                        Assert.False(Holds(versions[j].Graph, r.Value), $"{r.Value} reclaimed while version {j} (>= {k}) holds it");
                    Assert.False(Holds(d.Graph, r.Value));
                    Assert.True(released.Add(r.Value), $"{r.Value} reclaimed twice");
                }
            }
            versions.Add(d);

            // every payload ever published is in the graph, pending or released: exactly one of the three
            foreach (var n in d.Graph) published.Add(n.Value);
            var pending = d.Retired.Select(r => r.Value).ToHashSet(ReferenceEqualityComparer.Instance);
            Assert.Equal(d.RetiredCount, pending.Count);
            gone.Clear();
            foreach (var p in published)
            {
                bool inGraph = Holds(d.Graph, p), isPending = pending.Contains(p), isReleased = released.Contains(p);
                Assert.True((inGraph ? 1 : 0) + (isPending ? 1 : 0) + (isReleased ? 1 : 0) == 1,
                    $"step {step}: {p} in graph {inGraph}, pending {isPending}, released {isReleased}");
                if (isPending) gone.Add((Payload)p);
            }
        }
        Assert.True(released.Count > 100, $"exercised: {released.Count} released");
    }

    private static bool Holds(ImmDag<int, string, Payload> g, Payload p)
    {
        foreach (var n in g) if (ReferenceEquals(n.Value, p)) return true;
        return false;
    }

    private static bool Holds(ImmDag<int, string, Payload>.Scope s, Payload p)
    {
        for (var id = 0; id < Ids; id++) if (s.TryGetValue(id, out var v) && ReferenceEquals(v, p)) return true;
        return false;
    }
}

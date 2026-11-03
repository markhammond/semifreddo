using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>Regressions for issues found reviewing the library before its first release.</summary>
public class ReviewRegressionTests
{
    [Fact]
    public void Map_TryAddOnAnExistingKey_KeepsTheVersion_AndAllocatesNothing()
    {
        var m = ImmMap.CreateRange(Enumerable.Range(0, 1000).Select(i => KeyValuePair.Create(i, i)));
        var m2 = m.Mutate(s => s.TryAdd(5, 99));
        Assert.True(m2.IsSameAs(m));
        Assert.Equal(5, m2[5]);

        var shared = m;
        Assert.False(ImmInterlocked.Update(ref shared, static x => x.Mutate(s => s.TryAdd(5, 99))));   // no change: no swap
        Assert.True(shared.IsSameAs(m));

        var t = m.ToTransient();
        t.TryAdd(5, 99);                                    // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(t.TryAdd(6, 99));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Overwriting_WithAnEqualKey_KeepsTheStoredKey_PersistentAndTransientAlike()
    {
        string k1 = new('k', 3), k2 = new('k', 3);          // equal, distinct instances
        Assert.NotSame(k1, k2);

        var map = ImmMap<string, int>.Empty.SetItem(k1, 1).SetItem(k2, 2);
        Assert.Same(k1, map.Single().Key);
        Assert.Equal(2, map[k1]);
        Assert.Same(k1, ImmMap<string, int>.Empty.SetItem(k1, 1).Mutate(s => s[k2] = 2).Single().Key);

        var set = ImmSet<string>.Empty.Add(k1).Add(k2);
        Assert.Same(k1, set.Single());

        var ordered = ImmOrderedMap<string, int>.Empty.SetItem(k1, 1).SetItem(k2, 2);
        Assert.Same(k1, ordered.Single().Key);
        Assert.Equal(2, ordered[k1]);
    }

    [Fact]
    public void Check_OnASubsetOfTheNodes_IgnoresEdgesLeavingIt()
    {
        var dag = ImmDag<int, int, NoPayload>.Empty.AddNode(1, 1).AddNode(2, 2).AddNode(3, 3).AddEdge(1, 2).AddEdge(2, 3);
        Assert.True(Check.IsAcyclic(dag, [2, 3]));          // a predecessor outside: once a false cycle
        Assert.True(Check.IsAcyclic(dag, [1, 2]));          // a successor outside: once a KeyNotFoundException
        Assert.Equal(new Dictionary<int, int> { [1] = 0, [2] = 1 }, Check.ExactLevels(dag, [1, 2]));
        Assert.Equal(new Dictionary<int, int> { [2] = 0, [3] = 1 }, Check.ExactLevels(dag, [2, 3]));
        Assert.False(Check.IsAcyclic(dag.Mutate(static s => s.AddEdge(3, 1), new DagBatchOptions { SkipCycleCheck = true }), [1, 2, 3]));   // a real cycle still found
    }

    [Fact]
    public void FirstAndLast_OfAnEmptyCollection_ThrowInvalidOperation_Alike()
    {
        Assert.Throws<InvalidOperationException>(() => ImmVector<int>.Empty.Last);
        Assert.Throws<InvalidOperationException>(() => ImmFlexVector<int>.Empty.First);   // once an index error
        Assert.Throws<InvalidOperationException>(() => ImmFlexVector<int>.Empty.Last);
        Assert.Throws<InvalidOperationException>(() => ImmOrderedMap<int, int>.Empty.First);
        Assert.Equal(7, ImmFlexVector<int>.Empty.Add(5).Add(7).Last);
    }

    [Fact]
    public void ChangeLog_UpdatedNodes_AreThoseWhosePayloadOrAlternateKeyChangedUnderTheSameId()
    {
        var dag = ImmDag<int, string, string>.Empty.AddNode(1, "a", "A").AddNode(2, "b", "B").AddNode(3, "c", "C");
        static int[] Updated(ImmDag<int, string, string> d, Action<ImmDag<int, string, string>.Scope> batch)
        {
            int[] seen = [];
            d.Mutate(batch, (s, b) => { b(s); seen = s.UpdatedNodes.ToArray(); });
            return seen;
        }

        Assert.Equal([1], Updated(dag, static s => s.SetValue(1, "A2")));
        Assert.Empty(Updated(dag, static s => s.SetValue(1, "A")));                          // the same payload: no change
        Assert.Equal([2], Updated(dag, static s => { s.RemoveNode(2); s.AddNode(2, "b", "B2"); }));
        Assert.Empty(Updated(dag, static s => { s.RemoveNode(2); s.AddNode(2, "b", "B"); }));
        Assert.Equal([2], Updated(dag, static s => { s.RemoveNode(2); s.ReplaceNode(1, 2); }));  // renamed onto 2's id
        Assert.Equal([3], Updated(dag, static s => s.ReplaceNode(3, 3, "c2", "C")));            // a new alternate key
        Assert.Empty(Updated(dag, static s => { s.ReplaceNode(3, 30); s.AddNode(4, "d", "D"); })); // renames and adds: not updates
        Assert.Empty(Updated(dag, static s => s.RemoveNode(1)));
    }
}

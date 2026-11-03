using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

public class FactoryTests
{
    [Fact]
    public void Factories_NameTheirPolicies_AndReturnTheEmptyValues()
    {
        Assert.IsType<ImmDag<int, string, NoPayload>>(ImmDag.Create<int, string>());
        Assert.True(ImmDag.Create<int, string>().IsEmpty);
        Assert.IsType<ImmDag<int, string, double>>(ImmDag.Create<int, string, double>());

        Assert.IsType<ImmPkTopoOrder<int, MinValueSentinel<int>>>(ImmPkTopoOrder.CreateWithMinValueSentinel<int>());
        Assert.IsType<ImmPkTopoOrder<string, DefaultSentinel<string>>>(ImmPkTopoOrder.CreateWithDefaultSentinel<string>());
        Assert.IsType<ImmLeveledPkTopoOrder<long, MinValueSentinel<long>>>(ImmLeveledPkTopoOrder.CreateWithMinValueSentinel<long>());
        Assert.IsType<ImmLeveledPkTopoOrder<string, DefaultSentinel<string>>>(ImmLeveledPkTopoOrder.CreateWithDefaultSentinel<string>());
    }

    [Fact]
    public void MinValueSentinel_KeepsZeroUsable_DefaultSentinel_SuitsReferenceIds()
    {
        var dag = ImmDag.Create<int, string>();
        var order = ImmPkTopoOrder.CreateWithMinValueSentinel<int>();
        dag = dag.AddNode(ref order, 0, "zero", default).AddNode(ref order, 1, "one", default).AddEdge(ref order, 1, 0);
        Assert.Equal([1, 0], order.ToArray());

        var named = ImmDag.Create<string, int>();
        var byName = ImmLeveledPkTopoOrder.CreateWithDefaultSentinel<string>();
        named = named.AddNode(ref byName, "cream", 1, default).AddNode(ref byName, "fold", 2, default).AddEdge(ref byName, "cream", "fold");
        Assert.Equal(["cream", "fold"], byName.ToArray());
        Check.Levels(named, ["cream", "fold"], byName);
    }

    [Fact]
    public void TheReadmeDagExample_RunsAsWritten()
    {
        var recipe = ImmDag.Create<int, string>();
        var order = ImmPkTopoOrder.CreateWithMinValueSentinel<int>();

        recipe = recipe.Mutate(ref order, static s =>
        {
            s.AddNode(1, "whip the cream");
            s.AddNode(2, "whisk the eggs and sugar");
            s.AddNode(3, "fold together");
            s.AddEdge(1, 3);
            s.AddEdge(2, 3);
        });
        recipe = recipe.AddNode(ref order, 4, "freeze", default).AddEdge(ref order, 3, 4);

        var refused = Assert.Throws<DagCycleException<int>>(() => recipe.AddEdge(ref order, 4, 1));
        Assert.Equal("1 -> 3 -> 4 -> 1", string.Join(" -> ", refused.Cycle));
        Assert.Equal("1, 2, 3, 4", string.Join(", ", order));
    }
}

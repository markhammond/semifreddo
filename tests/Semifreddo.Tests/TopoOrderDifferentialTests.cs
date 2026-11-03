using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>ImmPkTopoOrder variants vs the canonical Kahn oracle, via the Semifreddo.Testing differential harness.</summary>
public class TopoOrderDifferentialTests
{
    private static void AssertUseful(DifferentialResult r) => Assert.True(r.AgreedSuccesses > 0 && r.AgreedFailures > 0, r.ToString());

    [Theory]
    [InlineData(1, 150)]
    [InlineData(2, 400)]
    [InlineData(3, 60)]
    public void Leveled_AgreesWithKahnOracle(int seed, int maxNodes) => AssertUseful(DifferentialHarness.RunLeveled(IntTopoOrder.Empty, seed, maxNodes: maxNodes));

    [Theory]
    [InlineData(6, 150)]
    [InlineData(7, 60)]
    public void OrderOnly_AgreesWithKahnOracle(int seed, int maxNodes) => AssertUseful(DifferentialHarness.Run(IntOrderOnly.Empty, seed, maxNodes: maxNodes));

    [Fact]
    public void DefaultSentinel_AgreesWithKahnOracle() => AssertUseful(DifferentialHarness.RunLeveled(ImmLeveledPkTopoOrder<int, DefaultSentinel<int>>.Empty, seed: 8));

    /// <summary>Ranks are uint Base + position: start just below the wrap so trims, reorders and compactions cross it.</summary>
    [Theory]
    [InlineData(4, 120, uint.MaxValue - 40)]
    [InlineData(5, 300, uint.MaxValue)]
    public void UintRanks_WrapAround_AgreesWithKahnOracle(int seed, int maxNodes, uint startBase)
    {
        var t = IntTopoOrder.Empty.ToTransient();
        t.Rebase(startBase);
        AssertUseful(DifferentialHarness.RunLeveled(t.ToPersistent(), seed, maxNodes: maxNodes));
    }

    [Fact] // the harness is generic over ILeveledTopologicalOrder: the oracle satisfies the same contract
    public void Oracle_RunsThroughLeveledHarness() => AssertUseful(DifferentialHarness.RunLeveled(KahnTopoOrder<int>.Empty, seed: 5, steps: 400));
}

namespace Semifreddo.Comparison;

/// <summary>The sizes every suite runs at: 1,000 and 100,000, or only those listed in <c>SEMIFREDDO_BENCH_N</c>
/// (comma-separated), e.g. <c>100000</c> for the README's tables.</summary>
public static class Sizes
{
    public static IEnumerable<int> N
        => Environment.GetEnvironmentVariable("SEMIFREDDO_BENCH_N") is { Length: > 0 } list
            ? list.Split(',').Select(int.Parse)
            : [1_000, 100_000];
}

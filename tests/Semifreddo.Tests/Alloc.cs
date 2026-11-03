namespace Semifreddo.Tests;

/// <summary>
/// Steady-state allocation probe. Warm-up runs let tiered JIT / OSR / PGO settle (their bookkeeping can be
/// charged to the running thread while many tests execute in parallel); the minimum over several trials is
/// what the code itself allocates.
/// </summary>
internal static class Alloc
{
    public static long SteadyState(Action body, int warmup = 5, int trials = 5)
    {
        for (var i = 0; i < warmup; i++) body();
        var min = long.MaxValue;
        for (var i = 0; i < trials; i++)
        {
            var a = GC.GetAllocatedBytesForCurrentThread();
            body();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
        }
        return min;
    }
}

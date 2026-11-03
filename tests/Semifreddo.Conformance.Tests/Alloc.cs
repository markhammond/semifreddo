namespace Semifreddo.Conformance;

/// <summary>Steady-state allocation probe: warm-ups let tiered JIT settle; the minimum over trials is what the code allocates.</summary>
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

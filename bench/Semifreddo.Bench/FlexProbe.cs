using System.Diagnostics;
using Semifreddo;
using Semifreddo.Internal;

/// <summary>
/// RRB shape and indexing cost: dense vs concat-relaxed vs random-insert trees.
/// Run with: dotnet run -c Release --project bench/Semifreddo.Bench -- flexprobe
/// </summary>
internal static class FlexProbe
{
    public static void Run()
    {
        const int N = 1_000_000, K = 20_000;
        var rnd = new Random(42);
        var src = Enumerable.Range(0, N).Select(i => (long)i).ToArray();
        var dense = ImmFlexVector.Create<long>(src);
        var after = dense;
        for (var i = 0; i < K; i++) after = after.Insert(rnd.Next(after.Count), -i);
        var concat = ImmFlexVector<long>.Empty;
        for (var k = 0; k < N;) { var m = Math.Min(N - k, 1 + rnd.Next(100)); concat += ImmFlexVector.Create<long>(src.AsSpan(k, m)); k += m; }
        var idx = Enumerable.Range(0, N).Select(_ => rnd.Next(N)).ToArray();

        Stats("dense", dense);
        Stats("concat-relaxed (1-100 pieces)", concat);
        Stats("after 20k random inserts", after);
        Time("dense            indexer", idx, i => dense[i]);
        Time("concat-relaxed   indexer", idx, i => concat[i]);
        Time("after-inserts    indexer", idx, i => after[i]);
    }

    private static void Time(string name, int[] idx, Func<int, long> get)
    {
        var best = double.MaxValue; long s = 0;
        for (var r = 0; r < 7; r++)
        {
            var sw = Stopwatch.StartNew();
            s = 0;
            foreach (var i in idx) s += get(i);
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / idx.Length);
        }
        Console.WriteLine($"{name,-34} {best,7:F1} ns ({s})");
    }

    private static void Stats(string name, ImmFlexVector<long> v)
    {
        var h = v.Head!;
        int inner = 0, relaxed = 0, leaves = 0; long leafFill = 0;
        void Walk(FNode<long> n, int shift)
        {
            if (shift == 0) { leaves++; leafFill += n.Count; return; }
            var b = FTree<long>.AsBranch(n); inner++; if (b is FRelaxed<long>) relaxed++;
            for (var j = 0; j < b.Len; j++) Walk(b.C[j]!, shift - 5);
        }
        Walk(h.Root!, h.Shift);
        Console.WriteLine($"{name,-32} height={h.Shift / 5} branches={inner} relaxed={relaxed} leaves={leaves} avgLeafFill={(double)leafFill / leaves:F1}");
    }

    /// <summary>Direct-path vs split+concat cost per RemoveRange size, to place the hybrid's cut-over.</summary>
    public static void RemoveCut()
    {
        const int N = 1_000_000;
        var rnd = new Random(3);
        var v0 = ImmFlexVector.Create<long>(Enumerable.Range(0, N).Select(i => (long)i).ToArray());
        var pos = Enumerable.Range(0, 4096).Select(_ => rnd.Next(N / 2)).ToArray();
        foreach (var n in new[] { 8, 16, 32, 64, 96, 128, 192, 256 })
        {
            double Measure(int cut, bool transient)
            {
                FTree<long>.DirectRemoveMaxPersistent = FTree<long>.DirectRemoveMaxTransient = cut;
                var best = double.MaxValue;
                for (var r = 0; r < 5; r++)
                {
                    var sw = Stopwatch.StartNew();
                    if (transient)
                        _ = v0.Mutate((pos, n), static (s, a) => { foreach (var p in a.pos) s.RemoveRange(p % (s.Count - a.n), a.n); });
                    else
                        foreach (var p in pos) _ = v0.RemoveRange(p, n);
                    best = Math.Min(best, sw.Elapsed.TotalNanoseconds / pos.Length);
                }
                return best;
            }
            Console.WriteLine($"n={n,4}: persistent direct {Measure(int.MaxValue, false),8:F0} ns  split+concat {Measure(0, false),8:F0} ns | " +
                              $"transient direct {Measure(int.MaxValue, true),8:F0} ns  split+concat {Measure(0, true),8:F0} ns");
        }
    }
}

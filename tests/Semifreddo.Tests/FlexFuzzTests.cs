using Semifreddo.Internal;
using Xunit; using Xunit.Abstractions;
namespace Semifreddo.Tests;
public class FlexFuzzTests(ITestOutputHelper o)
{
    static void Stats<T>(FNode<T> n, int shift, ref int relaxed, ref int dense, ref int maxScan)
    {
        if (shift == 0) return;
        var b = FTree<T>.AsBranch(n);
        if (b is FRelaxed<T> r)
        {
            relaxed++;
            for (var j = 0; j < r.Len; j++) { var idx = j == 0 ? 0 : r.Sz[j - 1]; maxScan = Math.Max(maxScan, j - (idx >> shift)); }
        }
        else dense++;
        for (var j = 0; j < b.Len; j++) Stats(b.C[j]!, shift - 5, ref relaxed, ref dense, ref maxScan);
    }
    [Fact] public void FuzzManySeeds()
    {
        int worstScan = 0, maxShift = 0, totalRelaxed = 0;
        for (var seed = 100; seed < 160; seed++)
        {
            var rnd = new Random(seed); var v = ImmFlexVector<int>.Empty; var model = new List<int>(); var seq = 0;
            for (var op = 0; op < 1500; op++)
            {
                int r = rnd.Next(10), c = model.Count;
                if (r < 3) { var arr = Enumerable.Range(seq, rnd.Next(1, 2000)).ToArray(); seq += arr.Length; var piece = ImmFlexVector.Create<int>(arr);
                             var i = rnd.Next(c + 1); v = v.InsertRange(i, piece); model.InsertRange(i, arr); }
                else if (r < 5 && c > 0) { int i = rnd.Next(c), n = rnd.Next(Math.Min(500, c - i) + 1); v = v.RemoveRange(i, n); model.RemoveRange(i, n); }
                else if (r < 7) { var i = rnd.Next(c + 1); v = v.Insert(i, seq); model.Insert(i, seq++); if (c > 0) { var j = rnd.Next(c + 1); v = v.RemoveAt(j); model.RemoveAt(j); } }
                else if (r < 8) { var s = rnd.Next(c + 1); v = v.Skip(s) + v.Take(s); model = model.Skip(s).Concat(model.Take(s)).ToList(); }
                else { v = v.Add(seq); model.Add(seq++); }
                if (v.Count > 300_000) { v = v.Take(10_000); model = model.Take(10_000).ToList(); }
            }
            FlexVectorTests.CheckInvariants(v);
            Assert.Equal(model, v.ToArray());
            for (var k = 0; k < model.Count; k += 7) Assert.Equal(model[k], v[k]);
            int rel = 0, den = 0, scan = 0;
            if (v.Head?.Root is { } root) Stats(root, v.Head.Shift, ref rel, ref den, ref scan);
            worstScan = Math.Max(worstScan, scan); maxShift = Math.Max(maxShift, v.Head?.Shift ?? 0); totalRelaxed += rel;
        }
        o.WriteLine($"relaxed nodes seen={totalRelaxed} maxShift={maxShift} worst extra scan steps={worstScan}");
    }
}

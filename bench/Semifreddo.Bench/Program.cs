// Quick-and-dirty harness (ns/op, bytes/op). Use BenchmarkDotNet for publishable numbers.
using System.Collections.Immutable;
using System.Diagnostics;
using Semifreddo;
using Semifreddo.Bench;

if (args.Length > 0 && args[0] == "flexprobe") { FlexProbe.Run(); return; }
if (args.Length > 0 && args[0] == "removecut") { FlexProbe.RemoveCut(); return; }
if (args.Length > 0 && args[0] == "topo") { TopoBench.Run(); return; }
if (args.Length > 0 && args[0] == "dag") { DagBench.Run(); return; }
if (args.Length > 0 && args[0] is "enum" or "linq") { EnumBench.Run(); return; }
const int N = 1_000_000, M = 200_000;
var rnd = new Random(42);
var idx = Enumerable.Range(0, N).Select(_ => rnd.Next(N)).ToArray();
var keys = Enumerable.Range(0, M).Select(_ => rnd.NextInt64()).ToArray();

void Run(string name, int ops, Func<long> body)
{
    body(); body(); // warm-up: let tiered JIT / OSR / PGO reach steady state
    var best = double.MaxValue; long bytes = 0, r = 0;
    for (var trial = 0; trial < 3; trial++)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var a0 = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        r = body();
        sw.Stop();
        best = Math.Min(best, sw.Elapsed.TotalNanoseconds);
        bytes = GC.GetAllocatedBytesForCurrentThread() - a0;
    }
    Console.WriteLine($"{name,-48} {best / ops,10:F1} ns/op {(double)bytes / ops,9:F1} B/op ({r})");
}

Console.WriteLine($"--- vector<long>, N={N:N0}");
Run("ImmVector.Add (persistent)", N, () => { var v = ImmVector<long>.Empty; for (var i = 0; i < N; i++) v = v.Add(i); return v.Count; });
Run("ImmutableList.Add (persistent)", N, () => { var v = ImmutableList<long>.Empty; for (var i = 0; i < N; i++) v = v.Add(i); return v.Count; });
Run("ImmVector.Transient.Add", N, () => { var t = ImmVector.CreateTransient<long>(); for (var i = 0; i < N; i++) t.Add(i); return t.ToPersistent().Count; });
Run("ImmutableList.Builder.Add", N, () => { var b = ImmutableList.CreateBuilder<long>(); for (var i = 0; i < N; i++) b.Add(i); return b.ToImmutable().Count; });
Run("List.Add", N, () => { var l = new List<long>(); for (var i = 0; i < N; i++) l.Add(i); return l.Count; });

var vec = ImmVector.Create<long>(Enumerable.Range(0, N).Select(i => (long)i).ToArray());
var il = ImmutableList.CreateRange(Enumerable.Range(0, N).Select(i => (long)i));
var list = Enumerable.Range(0, N).Select(i => (long)i).ToList();
Run("ImmVector[i] random", N, () => { long s = 0; foreach (var i in idx) s += vec[i]; return s; });
Run("ImmutableList[i] random", N, () => { long s = 0; foreach (var i in idx) s += il[i]; return s; });
Run("List[i] random", N, () => { long s = 0; foreach (var i in idx) s += list[i]; return s; });
Run("ImmVector foreach", N, () => { long s = 0; foreach (var x in vec) s += x; return s; });
Run("ImmVector.Chunks span scan", N, () => { long s = 0; foreach (var c in vec.Chunks) foreach (var x in c) s += x; return s; });
Run("ImmutableList foreach", N, () => { long s = 0; foreach (var x in il) s += x; return s; });
Run("List foreach", N, () => { long s = 0; foreach (var x in list) s += x; return s; });
Run("ImmVector.SetItem random (persistent)", N, () => { var v = vec; foreach (var i in idx) v = v.SetItem(i, i); return v[idx[0]]; });
Run("ImmutableList.SetItem random (persistent)", N, () => { var v = il; foreach (var i in idx) v = v.SetItem(i, i); return v[idx[0]]; });
Run("ImmVector.Mutate ItemRef += (batch)", N, () => vec.Mutate(idx, static (t, ix) => { foreach (var i in ix) t.ItemRef(i) += 1; })[idx[0]]);

Console.WriteLine($"--- map<long,long>, M={M:N0}");
Run("ImmMap.SetItem (persistent)", M, () => { var m = ImmMap<long, long>.Empty; foreach (var k in keys) m = m.SetItem(k, k); return m.Count; });
Run("ImmutableDictionary.SetItem (persistent)", M, () => { var m = ImmutableDictionary<long, long>.Empty; foreach (var k in keys) m = m.SetItem(k, k); return m.Count; });
Run("ImmMap.Transient upsert", M, () => { var t = ImmMap.CreateTransient<long, long>(); foreach (var k in keys) t.GetValueRefOrAddDefault(k, out _) = k; return t.ToPersistent().Count; });
Run("ImmutableDictionary.Builder", M, () => { var b = ImmutableDictionary.CreateBuilder<long, long>(); foreach (var k in keys) b[k] = k; return b.ToImmutable().Count; });
Run("Dictionary", M, () => { var d = new Dictionary<long, long>(); foreach (var k in keys) d[k] = k; return d.Count; });

var map = ImmMap.CreateRange(keys.Select(k => KeyValuePair.Create(k, k)));
var idict = keys.ToImmutableDictionary(k => k, k => k);
var dict = keys.ToDictionary(k => k, k => k);
Run("ImmMap.TryGetValue", M, () => { long s = 0; foreach (var k in keys) if (map.TryGetValue(k, out var v)) s += v & 1; return s; });
Run("ImmutableDictionary.TryGetValue", M, () => { long s = 0; foreach (var k in keys) if (idict.TryGetValue(k, out var v)) s += v & 1; return s; });
Run("Dictionary.TryGetValue", M, () => { long s = 0; foreach (var k in keys) if (dict.TryGetValue(k, out var v)) s += v & 1; return s; });
Run("ImmMap foreach", M, () => { long s = 0; foreach (var kv in map) s += kv.Value & 1; return s; });
Run("ImmutableDictionary foreach", M, () => { long s = 0; foreach (var kv in idict) s += kv.Value & 1; return s; });
Run("ImmMap.Mutate accumulate (owned path)", M, () => map.Mutate(keys, static (t, ks) => { foreach (var k in ks) t.GetValueRefOrAddDefault(k, out _) += 1; foreach (var k in ks) t.GetValueRefOrAddDefault(k, out _) += 1; }).Count);
Run("ImmMap.Remove all (persistent)", M, () => { var m = map; foreach (var k in keys) m = m.Remove(k); return m.Count; });
Run("ImmutableDictionary.Remove all (persistent)", M, () => { var m = idict; foreach (var k in keys) m = m.Remove(k); return m.Count; });

Console.WriteLine($"--- flex_vector<long>, N={N:N0}");
Run("ImmFlexVector.Add (persistent)", N, () => { var v = ImmFlexVector<long>.Empty; for (var i = 0; i < N; i++) v = v.Add(i); return v.Count; });
Run("ImmFlexVector transient Add", N, () => { var t = ImmFlexVector.CreateTransient<long>(); for (var i = 0; i < N; i++) t.Add(i); return t.ToPersistent().Count; });
var src = Enumerable.Range(0, N).Select(i => (long)i).ToArray();
var fdense = ImmFlexVector.Create<long>(src);
var frelaxed = ImmFlexVector<long>.Empty;
for (var k = 0; k < N;) { var m = Math.Min(N - k, 1 + rnd.Next(100)); frelaxed += ImmFlexVector.Create<long>(src.AsSpan(k, m)); k += m; }
Run("ImmFlexVector[i] random (dense)", N, () => { long s = 0; foreach (var i in idx) s += fdense[i]; return s; });
Run("ImmFlexVector[i] random (relaxed, 1-100 concats)", N, () => { long s = 0; foreach (var i in idx) s += frelaxed[i]; return s; });
Run("ImmFlexVector.Chunks scan (relaxed)", N, () => { long s = 0; foreach (var c in frelaxed.Chunks) foreach (var x in c) s += x; return s; });
const int K = 20_000;
Run("ImmFlexVector a + b (500k + 500k)", K, () => { long s = 0; var a = fdense.Take(N / 2); var b = fdense.Skip(N / 2); for (var i = 0; i < K; i++) s += (a + b).Count; return s; });
Run("ImmutableList.AddRange (500k + 500k)", 10, () => { long s = 0; var a = il.GetRange(0, N / 2); var b = il.GetRange(N / 2, N / 2); for (var i = 0; i < 10; i++) s += a.AddRange(b).Count; return s; });
Run("ImmFlexVector.Slice(250k, 500k)", K, () => { long s = 0; for (var i = 0; i < K; i++) s += fdense.Slice(250_000, 500_000).Count; return s; });
Run("ImmutableList.GetRange(250k, 500k)", 10, () => { long s = 0; for (var i = 0; i < 10; i++) s += il.GetRange(250_000, 500_000).Count; return s; });
Run("ImmFlexVector.Insert(random) (persistent)", K, () => { var v = fdense; for (var i = 0; i < K; i++) v = v.Insert(idx[i], i); return v.Count; });
Run("ImmutableList.Insert(random) (persistent)", K, () => { var v = il; for (var i = 0; i < K; i++) v = v.Insert(idx[i], i); return v.Count; });
Run("List.Insert(random)", K, () => { var l = new List<long>(list); for (var i = 0; i < K; i++) l.Insert(idx[i], i); return l.Count; });

Console.WriteLine($"--- ordered map<long,long>, M={M:N0}");
Run("ImmOrderedMap.SetItem (persistent)", M, () => { var m = ImmOrderedMap<long, long>.Empty; foreach (var k in keys) m = m.SetItem(k, k); return m.Count; });
Run("ImmOrderedMap transient upsert", M, () => ImmOrderedMap<long, long>.Empty.Mutate(keys, static (s, ks) => { foreach (var k in ks) s[k] = k; }).Count);
Run("OrderedDictionary (mutable)", M, () => { var d = new OrderedDictionary<long, long>(); foreach (var k in keys) d[k] = k; return d.Count; });
var om = ImmOrderedMap<long, long>.Empty.Mutate(keys, static (s, ks) => { foreach (var k in ks) s[k] = k; });
var od = new OrderedDictionary<long, long>(); foreach (var k in keys) od[k] = k;
Run("ImmOrderedMap.TryGetValue", M, () => { long s = 0; foreach (var k in keys) if (om.TryGetValue(k, out var v)) s += v & 1; return s; });
Run("OrderedDictionary.TryGetValue", M, () => { long s = 0; foreach (var k in keys) if (od.TryGetValue(k, out var v)) s += v & 1; return s; });
Run("ImmOrderedMap foreach (insertion order)", M, () => { long s = 0; foreach (var kv in om) s += kv.Value & 1; return s; });
Run("ImmOrderedMap FIFO: add + remove oldest", M, () => { var m = om; foreach (var k in keys) { m = m.SetItem(~k, k); m = m.Remove(m.First.Key); } return m.Count; });
Run("ImmOrderedMap remove random (interior)", M, () => { var m = om; foreach (var k in keys) m = m.Remove(k); return m.Count; });
Run("OrderedDictionary remove random (O(n) shift)", 20_000, () => { var d = new OrderedDictionary<long, long>(od); for (var i = 0; i < 20_000; i++) d.Remove(keys[i]); return d.Count; });

Console.WriteLine("--- alternate lookup, string keys (8 chars)");
var syms = Enumerable.Range(0, M).Select(i => $"S{i:D7}").ToArray();
var wire = string.Concat(syms).ToCharArray();
var smap = ImmMap<string, long>.Empty.Mutate(syms, static (s, ks) => { foreach (var k in ks) s[k] = 1; });
var salt = smap.GetAlternateLookup<ReadOnlySpan<char>>();
var sdict = syms.ToDictionary(k => k, _ => 1L);
var sdictAlt = sdict.GetAlternateLookup<ReadOnlySpan<char>>();
Run("ImmMap<string>.TryGetValue(string)", M, () => { long s = 0; foreach (var k in syms) if (smap.TryGetValue(k, out var v)) s += v; return s; });
Run("ImmMap<string> alt TryGetValue(span)", M, () => { long s = 0; for (var i = 0; i < M; i++) if (salt.TryGetValue(wire.AsSpan(i * 8, 8), out var v)) s += v; return s; });
Run("Dictionary<string> alt TryGetValue(span)", M, () => { long s = 0; for (var i = 0; i < M; i++) if (sdictAlt.TryGetValue(wire.AsSpan(i * 8, 8), out var v)) s += v; return s; });
Run("new string(span) + ImmMap.TryGetValue", M, () => { long s = 0; for (var i = 0; i < M; i++) if (smap.TryGetValue(new string(wire.AsSpan(i * 8, 8)), out var v)) s += v; return s; });
Run("ImmMap transient alt upsert (existing keys)", M, () => smap.Mutate(wire, static (sc, w) => { var a = sc.GetAlternateLookup<ReadOnlySpan<char>>(); for (var i = 0; i < w.Length / 8; i++) a.GetValueRefOrAddDefault(w.AsSpan(i * 8, 8), out _) += 1; }).Count);

Console.WriteLine("--- flex_vector insert/remove at random positions (persistent), then indexing");
{
    var baseV = ImmFlexVector.Create<long>(src);
    var after = baseV;
    Run("ImmFlexVector.Insert(random) direct path", K, () => { var v = baseV; for (var i = 0; i < K; i++) v = v.Insert(idx[i], i); after = v; return v.Count; });
    Run("ImmFlexVector.RemoveAt(random) direct path", K, () => { var v = baseV; for (var i = 0; i < K; i++) v = v.RemoveAt(idx[i] % v.Count); return v.Count; });
    Run("ImmutableList.RemoveAt(random)", K, () => { var v = il; for (var i = 0; i < K; i++) v = v.RemoveAt(idx[i] % v.Count); return v.Count; });
    Run("ImmFlexVector[i] after 20k random inserts", N, () => { long s = 0; foreach (var i in idx) s += after[i]; return s; });
}

Console.WriteLine("--- DAG: 100k nodes (int id <-> int alt key), 200k forward edges, built in one batch");
{
    const int NN = 100_000, EE = 200_000;
    var er = new Random(7);
    var edges = new (int, int)[EE];
    for (var i = 0; i < EE; i++) { var a = er.Next(NN - 1); var b = a + 1 + er.Next(Math.Min(50, NN - a - 1)); edges[i] = (a, b); }
    ImmDag<int, int, NoPayload> dag = default;
    Run("ImmDag batch build (+ cycle check)", NN + EE, () =>
    {
        dag = ImmDag<int, int, NoPayload>.Empty.Mutate(edges, static (s, es) =>
        {
            for (var i = 0; i < NN; i++) s.AddNode(i, 1_000_000_000 - i);
            foreach (var (a, b) in es) s.AddEdge(a, b);
        });
        return dag.EdgeCount;
    });
    ImmOrderedMap<int, int> fwd = default, rev = default; ImmMap<int, ImmSet<int>> outs = default, ins = default;
    Run("composed: 2x ImmOrderedMap + 2x ImmMap<ImmSet>", NN + EE, () =>
    {
        fwd = ImmOrderedMap<int, int>.Empty.Mutate(static s => { for (var i = 0; i < NN; i++) s[i] = 1_000_000_000 - i; });
        rev = ImmOrderedMap<int, int>.Empty.Mutate(static s => { for (var i = 0; i < NN; i++) s[1_000_000_000 - i] = i; });
        outs = ImmMap<int, ImmSet<int>>.Empty.Mutate(edges, static (s, es) => { foreach (var (a, b) in es) { ref var x = ref s.GetValueRefOrAddDefault(a, out _); x = x.Add(b); } });
        ins = ImmMap<int, ImmSet<int>>.Empty.Mutate(edges, static (s, es) => { foreach (var (a, b) in es) { ref var x = ref s.GetValueRefOrAddDefault(b, out _); x = x.Add(a); } });
        return outs.Count;
    });
    var probes = Enumerable.Range(0, NN).Select(_ => er.Next(NN)).ToArray();
    Run("ImmDag: alt key -> id -> successor count", NN, () => { long s = 0; foreach (var p in probes) if (dag.TryGetNodeByAlternateKey(1_000_000_000 - p, out var id)) s += dag.Successors(id).Count; return s; });
    Run("composed: alt key -> id -> successor count", NN, () => { long s = 0; foreach (var p in probes) if (rev.TryGetValue(1_000_000_000 - p, out var id)) s += outs.GetValueOrDefault(id).Count; return s; });
    Run("ImmDag: persistent AddEdge (+ reachability check)", 2000, () => { var d = dag; for (var i = 0; i < 2000; i++) { var a = probes[i] % (NN - 60); d = d.AddEdge(a, a + 55); } return d.EdgeCount; });
    Run("composed: persistent add edge (no cycle check)", 2000, () => { var o = outs; var n = ins; for (var i = 0; i < 2000; i++) { var a = probes[i] % (NN - 60); o = o.SetItem(a, o.GetValueOrDefault(a).Add(a + 55)); n = n.SetItem(a + 55, n.GetValueOrDefault(a + 55).Add(a)); } return o.Count + n.Count; });
    Run("EdgeSet: 5,000 adds, persistent copy per add (old)", 5000, () => { var e = default(EdgeSet<int>); for (var i = 0; i < 5000; i++) e = e.Add(i, null, out _); return e.Count; });
    Run("EdgeSet: 5,000 adds, owned in place (batch)", 5000, () => { var o = new Semifreddo.Internal.ChampOwner<Semifreddo.Internal.SetEntry<int>>(new()); var e = default(EdgeSet<int>); for (var i = 0; i < 5000; i++) e = e.Add(i, o, out _); return e.Count; });
    var hubBase = ImmDag<int, int, NoPayload>.Empty.Mutate(static s => { for (var i = 0; i <= 5000; i++) s.AddNode(i, i); }, new DagBatchOptions { SkipCycleCheck = true });
    Run("ImmDag: hub with 5,000 successors, one batch", 5000, () => hubBase.Mutate(static s => { for (var i = 1; i <= 5000; i++) s.AddEdge(0, i); }, new DagBatchOptions { SkipCycleCheck = true }).EdgeCount);
    Run("ImmDag: RemoveNode (incident edges via in-index)", 2000, () => { var d = dag; for (var i = 0; i < 2000; i++) d = d.RemoveNode(probes[i]); return d.NodeCount; });
}

Console.WriteLine("--- flex_vector removal: RemoveRange hybrid (persistent unless noted)");
{
    var baseV = ImmFlexVector.Create<long>(src);
    foreach (var n in new[] { 1, 8, 16, 17, 64, 1000 })
    {
        var ops = Math.Min(K, N / 2 / n); // never drain the vector
        Run($"ImmFlexVector.RemoveRange(random, {n})", ops, () => { var v = baseV; for (var i = 0; i < ops; i++) v = v.RemoveRange(idx[i] % (v.Count - n), n); return v.Count; });
    }
    Run("ImmutableList.RemoveRange(random, 16)", K, () => { var v = il; for (var i = 0; i < K; i++) v = v.RemoveRange(idx[i] % (v.Count - 16), 16); return v.Count; });
    Run("ImmFlexVector transient RemoveAt(random)", K, () => baseV.Mutate(idx, static (s, ix) => { for (var i = 0; i < K; i++) s.RemoveAt(ix[i] % s.Count); }).Count);
    var thinned = baseV.Mutate(idx, static (s, ix) => { for (var i = 0; i < 700_000; i++) s.RemoveAt(ix[i % ix.Length] % s.Count); });
    var probe = Enumerable.Range(0, N).Select(i => idx[i] % thinned.Count).ToArray();
    Run("ImmFlexVector[i] after removing 70% at random", N, () => { long s = 0; foreach (var i in probe) s += thinned[i]; return s; });
    Console.WriteLine($"  (thinned: count={thinned.Count:N0} shift={thinned.Head!.Shift})");
}

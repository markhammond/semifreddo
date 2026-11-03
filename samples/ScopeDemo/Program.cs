using System.Diagnostics;
using System.Reflection;
using Semifreddo;

var dbg = Assembly.GetExecutingAssembly().GetCustomAttribute<DebuggableAttribute>();
var lib = typeof(ImmVector).Assembly.GetCustomAttribute<DebuggableAttribute>();
Console.WriteLine($"JIT optimiser disabled? sample={dbg?.IsJITOptimizerDisabled ?? false} library={lib?.IsJITOptimizerDisabled ?? false}");

// ---------------------------------------------------------------- 1. worked example (all verified at runtime)

// (a) The lambda's first parameter is a ref struct (ImmVector<long>.Scope). `static` => no closure, cached delegate.
var grams = ImmVector.Create<long>(Enumerable.Range(0, 1_000).Select(i => 10_000L + i).ToArray());   // batch weights
var adjusted = grams.Mutate(5L, static (s, topUp) =>
{
    for (var i = 0; i < s.Count; i++) s.ItemRef(i) += topUp;  // in-place via ref; refs can't leave the lambda
    s.Add(-1);
});
Check(adjusted.Count == 1_001 && adjusted[0] == 10_005 && grams[0] == 10_000, "vector batch + persistence");

// (b) TArg itself can be a ref struct (ReadOnlySpan<T>): Mutate<TArg> is `where TArg : allows ref struct`
//     and Action<T1,T2> is annotated `allows ref struct` since .NET 9 — so BOTH lambda parameters are ref structs.
ReadOnlySpan<long> moreBatches = [1, 2, 3, 4];
var appended = grams.Mutate(moreBatches, static (s, span) => s.AddRange(span));
Check(appended.Count == 1_004 && appended.Last == 4, "span argument");

// (c) Map: single-pass, ref-returning upsert inside the scope (tubs in stock by flavor id: deliveries and sales netted,
//     sold-out flavors dropped).
(int Flavor, long Tubs)[] movements = [(1, 100), (2, -50), (1, 25), (3, 10), (2, 50)];
var stock = ImmMap<int, long>.Empty.Mutate(movements, static (s, ms) =>
{
    foreach (var (flavor, tubs) in ms) s.GetValueRefOrAddDefault(flavor, out _) += tubs;
    foreach (var flavor in (ReadOnlySpan<int>)[1, 2, 3]) if (s[flavor] == 0) s.Remove(flavor);
});
Check(stock.Count == 2 && stock[1] == 125 && !stock.ContainsKey(2), "map netting");

// (d) Set
var universe = ImmSet<int>.Empty.Mutate(static s => { for (var i = 0; i < 100; i++) s.Add(i % 10); });
Check(universe.Count == 10, "set batch");

// (e) A no-op batch returns the identical instance (no allocation, IsSameAs).
Check(grams.Mutate(static s => _ = s.Count).IsSameAs(grams), "no-op identity");

// ---------------------------------------------------------------- 2. allocation verification

const int N = 1_000_000;
var big = ImmVector.Create<long>(Enumerable.Range(0, N).Select(i => (long)i).ToArray());
var idx = Enumerable.Range(0, N).Select(i => (int)((i * 7919L) % N)).ToArray();
var mapKeys = Enumerable.Range(0, 100_000).Select(i => i * 31).ToArray();
var bigMap = ImmMap.CreateRange(mapKeys.Select(k => KeyValuePair.Create(k, 0L)));
var longLived = big.ToTransient();
longLived.ItemRef(0) = 0; // owns path to leaf 0 only
foreach (var i in idx) longLived.ItemRef(i) += 0; // now owns the whole tree
long captured = 1;

Console.WriteLine($"\n{"scenario",-58}{"bytes",12}{"per op",10}");
Measure("vector: no-op Mutate", 1, () => big.Mutate(static s => _ = s.Count));
Measure("vector: Mutate writes 1 tail slot", 1, () => big.Mutate(static s => s.ItemRef(s.Count - 1) = 7));
Measure("vector: Mutate writes 1 tree slot (depth 4)", 1, () => big.Mutate(static s => s.ItemRef(12_345) = 7));
Measure("vector: Mutate 1M random ItemRef += 1", N, () => big.Mutate(idx, static (s, ix) => { foreach (var i in ix) s.ItemRef(i) += 1; }));
MeasureA("vector: long-lived Transient, tree already owned, 1M +=", N, () => { foreach (var i in idx) longLived.ItemRef(i) += 1; });
Measure("vector: Mutate AddRange(span) 1M", N, () => ImmVector<long>.Empty.Mutate(idx, static (s, ix) => { foreach (var c in ix.AsSpan().Chunk32()) s.AddRange(c); }));
Measure("vector: capturing lambda (closure per call), 1 tail write", 1, () => Capturing(big, captured));
Measure("vector: static lambda + TArg, 1 tail write", 1, () => big.Mutate(captured, static (s, x) => s.ItemRef(s.Count - 1) = x));
Measure("map: no-op Mutate", 1, () => bigMap.Mutate(static s => _ = s.Count));
Measure("map: Mutate 100k accumulate, existing keys (1st touch)", mapKeys.Length, () => bigMap.Mutate(mapKeys, static (s, ks) => { foreach (var k in ks) s.GetValueRefOrAddDefault(k, out _) += 1; }));
Measure("map: same batch x2 in one Mutate (2nd pass is owned)", 2 * mapKeys.Length, () => bigMap.Mutate(mapKeys, static (s, ks) => { for (var r = 0; r < 2; r++) foreach (var k in ks) s.GetValueRefOrAddDefault(k, out _) += 1; }));
Measure("map: Mutate 100k inserts into empty", mapKeys.Length, () => ImmMap<int, long>.Empty.Mutate(mapKeys, static (s, ks) => { foreach (var k in ks) s[k] = k; }));

static void Measure<TR>(string name, int ops, Func<TR> body)
{
    for (var i = 0; i < 3; i++) body(); // reach tier-1 / OSR before measuring
    var a = GC.GetAllocatedBytesForCurrentThread();
    var r = body();
    var b = GC.GetAllocatedBytesForCurrentThread() - a;
    GC.KeepAlive(r);
    Console.WriteLine($"{name,-58}{b,12:N0}{(double)b / ops,10:F2}");
}
static void MeasureA(string name, int ops, Action body) => Measure(name, ops, () => { body(); return 0; });
static ImmVector<long> Capturing(ImmVector<long> v, long x) => v.Mutate(s => s.ItemRef(s.Count - 1) = x);
static void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) Environment.Exit(1); }

static class Ext
{
    public static Chunks Chunk32(this Span<int> s) => new(s);
    public ref struct Chunks(Span<int> s)
    {
        private Span<int> _rest = s; private long[] _buf = new long[32]; private int _n;
        public Chunks GetEnumerator() => this;
        public bool MoveNext() { if (_rest.IsEmpty) return false; _n = Math.Min(32, _rest.Length); for (var i = 0; i < _n; i++) _buf[i] = _rest[i]; _rest = _rest[_n..]; return true; }
        public ReadOnlySpan<long> Current => _buf.AsSpan(0, _n);
    }
}

namespace Semifreddo.Testing;

/// <summary>Outcome counts of a <see cref="DifferentialHarness"/> run (a useful run has both kinds).</summary>
public readonly record struct DifferentialResult(int Steps, int AgreedSuccesses, int AgreedFailures);

/// <summary>
/// Runs an <see cref="ITopologicalOrder{TSelf, N}"/> implementation in lockstep with the canonical
/// <see cref="KahnTopoOrder{N}"/> oracle over a random scenario on an <see cref="ImmDag{N, A, V}"/>: node additions and
/// removals, renames, single edges in random directions (cycles included) and batches on both sides of typical
/// strategy switches. Every success / failure must agree (cycle-reporting and probe overloads alike), failures must
/// leave the order unchanged and report a real cycle, and both orders are checked against the graph periodically.
/// Node ids are 1-based, so any sentinel that reserves 0 or <c>int.MinValue</c> works.
/// </summary>
public static class DifferentialHarness
{
    /// <param name="start">An empty order (it may carry internal state, e.g. a non-zero base).</param>
    /// <param name="seed">Random seed: a failing seed reproduces the exact scenario.</param>
    /// <param name="steps">Number of random operations.</param>
    /// <param name="maxNodes">Upper bound on live nodes (smaller graphs are denser, so cycles are attempted more often).</param>
    /// <param name="validateEvery">Full validation interval in steps. Keep it at 1 unless scenarios are large: a defect
    /// that corrupts the order is then reported on the step that caused it, before later operations run on a broken
    /// order (where incremental algorithms may degrade badly instead of failing).</param>
    /// <param name="check">Extra checks run with the periodic validation (e.g. level invariants).</param>
    /// <param name="tighten">Optional normalisation applied to the implementation at random points (e.g. <c>Tighten</c>).</param>
    public static DifferentialResult Run<TOrder>(TOrder start, int seed, int steps = 1500, int maxNodes = 150,
        Action<ImmDag<int, int, NoPayload>, TOrder>? check = null,
        Func<TOrder, ImmDag<int, int, NoPayload>, TOrder>? tighten = null, int validateEvery = 1)
        where TOrder : ITopologicalOrder<TOrder, int>
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(validateEvery, 1);
        if (start.Count != 0) throw new ArgumentException("The start order must be empty.", nameof(start));
        var rnd = new Random(seed);
        var dag = ImmDag<int, int, NoPayload>.Empty;
        var order = start;
        var oracle = KahnTopoOrder<int>.Empty;
        int nextId = 1, successes = 0, failures = 0;

        void Validate()
        {
            Check.TopologicalOrder(dag, Ids(dag), order);
            Check.TopologicalOrder(dag, Ids(dag), oracle);
            var a = order.ToHashSet();
            if (!a.SetEquals(oracle)) throw new InvariantViolationException("order and oracle hold different nodes");
            check?.Invoke(dag, order);
        }

        void Outcome(bool ok, bool okOracle, bool okProbe, bool okOracleProbe, TOrder next, TOrder probe, ImmDag<int, int, NoPayload> attempted, int[]? cycle, int[]? oracleCycle)
        {
            if (ok != okOracle) throw new InvariantViolationException($"step disagreement: implementation {(ok ? "succeeded" : "failed")}, oracle {(okOracle ? "succeeded" : "failed")}");
            if (okProbe != ok || okOracleProbe != okOracle) throw new InvariantViolationException("probe overload disagrees with the cycle-reporting overload");
            if (ok)
            {
                if (!next.SequenceEqual(probe)) throw new InvariantViolationException("probe overload produced a different order");
                successes++;
                return;
            }
            failures++;
            if (!next.IsSameAs(order)) throw new InvariantViolationException("a failed edge operation changed the order");
            Check.Cycle<int, ImmDag<int, int, NoPayload>>(attempted, cycle!);
            Check.Cycle<int, ImmDag<int, int, NoPayload>>(attempted, oracleCycle!);
        }

        for (var step = 0; step < steps; step++)
        {
            var ids = Ids(dag);
            var r = rnd.Next(100);
            if ((r < 20 && ids.Length < maxNodes) || ids.Length < 3)
            {
                var id = nextId++;
                dag = dag.Mutate(id, static (s, x) => s.AddNode(x, x), new DagBatchOptions { SkipCycleCheck = true });
                order = order.Add(id);
                oracle = oracle.Add(id);
            }
            else if (r < 25)
            {
                var victim = ids[rnd.Next(ids.Length)];
                dag = dag.RemoveNode(victim);
                order = order.Remove(victim);
                oracle = oracle.Remove(victim);
            }
            else if (r < 27)
            {
                int old = ids[rnd.Next(ids.Length)], id = nextId++;
                dag = dag.ReplaceNode(old, id, id, default);
                order = order.Replace(old, id);
                oracle = oracle.Replace(old, id);
            }
            else if (r < 70)
            {
                int u = ids[rnd.Next(ids.Length)], w = ids[rnd.Next(ids.Length)];
                if (u == w || dag.ContainsEdge(u, w)) continue;
                var attempted = dag.Mutate((u, w), static (s, e) => s.AddEdge(e.u, e.w), new DagBatchOptions { SkipCycleCheck = true });
                var ok = order.TryAddEdge(attempted, u, w, out var next, out var cycle);
                var okO = oracle.TryAddEdge(attempted, u, w, out var nextO, out var cycleO);
                var okP = order.TryAddEdge(attempted, u, w, out var probe);
                var okOP = oracle.TryAddEdge(attempted, u, w, out _);
                Outcome(ok, okO, okP, okOP, next, probe, attempted, cycle, cycleO);
                if (ok) { dag = attempted; order = next; oracle = nextO; }
            }
            else
            {
                var k = rnd.Next(2) == 0 ? 1 + rnd.Next(8) : 33 + rnd.Next(60);
                var batch = new List<(int, int)>();
                for (var i = 0; i < k; i++)
                {
                    int u = ids[rnd.Next(ids.Length)], w = ids[rnd.Next(ids.Length)];
                    if (u == w) continue;
                    if (rnd.Next(3) != 0 && u > w) (u, w) = (w, u); // bias towards id-forward so some batches succeed
                    batch.Add((u, w));
                }
                var edges = batch.ToArray();
                var attempted = dag.Mutate(edges, static (s, es) => { foreach (var (u, w) in es) s.AddEdge(u, w); }, new DagBatchOptions { SkipCycleCheck = true });
                var ok = order.TryAddEdges(attempted, edges, out var next, out var cycle);
                var okO = oracle.TryAddEdges(attempted, edges, out var nextO, out var cycleO);
                var okP = order.TryAddEdges(attempted, edges, out var probe);
                var okOP = oracle.TryAddEdges(attempted, edges, out _);
                Outcome(ok, okO, okP, okOP, next, probe, attempted, cycle, cycleO);
                if (ok) { dag = attempted; order = next; oracle = nextO; }
            }

            if (tighten is not null && rnd.Next(40) == 0) order = tighten(order, dag);
            if (step % validateEvery == 0) Validate();
        }
        Validate();
        return new(steps, successes, failures);
    }

    /// <summary>
    /// <see cref="Run{TOrder}"/> for a leveled order (e.g. <see cref="ImmLeveledPkTopoOrder{N, TSentinel}"/>): also checks
    /// level invariants periodically, tightens at random points, and compares tightened levels with exact longest-path levels.
    /// </summary>
    public static DifferentialResult RunLeveled<TOrder>(TOrder start, int seed, int steps = 1500, int maxNodes = 150, int validateEvery = 1)
        where TOrder : ILeveledTopologicalOrder<TOrder, int>
        => Run(start, seed, steps, maxNodes, validateEvery: validateEvery,
            check: static (dag, order) =>
            {
                Check.Levels<int, TOrder, ImmDag<int, int, NoPayload>>(dag, Ids(dag), order);
                Check.Levels<int, TOrder, ImmDag<int, int, NoPayload>>(dag, Ids(dag), order.Tighten(dag), exact: true);
            },
            tighten: static (order, dag) => order.Tighten(dag));

    private static int[] Ids(ImmDag<int, int, NoPayload> dag) => dag.Select(n => n.Id).ToArray();
}

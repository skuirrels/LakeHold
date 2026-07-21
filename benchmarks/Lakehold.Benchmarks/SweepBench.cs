using System.Collections.Concurrent;

namespace Lakehold.Benchmarks;

/// <summary>
///     Finding #2: <c>DucklingPool.EvictIdleAsync</c> — throttle the idle sweep instead of running it
///     on every acquisition.
/// </summary>
/// <remarks>
///     Models the per-<c>GetOrStartAsync</c> cost of the sweep with a faithful stand-in for the real
///     one: a warm dictionary of N sessions keyed as production keys them, whose "before" arm walks
///     every entry and builds the same candidate list the real sweep allocates, and whose "after" arm
///     performs the throttle check that returns before allocating anything. The Ducklings themselves
///     are irrelevant to this cost — it is the enumeration and the list that the hot path paid per
///     query — so the entry value is a plain timestamp, standing in for <c>LastUsedUtc</c>.
/// </remarks>
public static class SweepBench
{
    public static void Run(int warmSessions)
    {
        var sessions = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < warmSessions; i++)
        {
            // All recently used: the common steady state, where nothing is evicted and the sweep's
            // whole cost is pure overhead.
            sessions[$"tenant-{i}/analytics"] = now.AddSeconds(-i % 30);
        }

        const int maxWarm = 32;
        var lastSweepTicks = now.UtcTicks;
        var sweepIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

        const int calls = 5_000_000;

        var before = Harness.MeasureSync("before", calls, () =>
        {
            // The real sweep's steady-state work: allocate the candidate list, enumerate every
            // session, and record the completed ones.
            var candidates = new List<(string Name, DateTimeOffset LastUsed)>();
            foreach (var (name, lastUsed) in sessions)
            {
                candidates.Add((name, lastUsed));
            }

            _ = candidates.Count; // consume, so nothing is optimised away
        });

        var after = Harness.MeasureSync("after", calls, () =>
        {
            // The throttle, mirroring production: an interlocked read and two comparisons, then
            // return before allocating anything. Only the caller that wins the CompareExchange below
            // goes on to sweep, so a burst arriving after the interval does not all sweep at once.
            var now = DateTimeOffset.UtcNow;
            var last = Interlocked.Read(ref lastSweepTicks);
            var overflowing = sessions.Count > maxWarm;

            if (now.UtcTicks - last < sweepIntervalTicks && !overflowing)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastSweepTicks, now.UtcTicks, last) != last && !overflowing)
            {
                return;
            }
        });

        Harness.PrintComparison(
            $"#2 Pool sweep — per GetOrStartAsync, {warmSessions} warm sessions (steady state)",
            "ns/call",
            before,
            after);
    }
}

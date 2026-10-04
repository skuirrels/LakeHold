using Lakehold.Querying;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Linq.Compiler.Tests;

public sealed class LinqCompilerReadinessTests
{
    // A readiness probe spawns a Roslyn worker process. A container health check calls it on a
    // fixed interval forever, so "verify once per window" is the behaviour under test: without it
    // an idle compiler burns a core compiling the same probe thousands of times a day.
    [Fact]
    public async Task Repeated_probes_within_the_cache_window_verify_once()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var compiler = new CountingCompilerProcess();
        var readiness = Create(compiler, clock, success: TimeSpan.FromMinutes(5));

        for (var probe = 0; probe < 25; probe++)
        {
            Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(15));
        }

        // 25 probes spanning 6 minutes of a 5-minute window: the first, and one after it expires.
        Assert.Equal(2, compiler.Compilations);
    }

    [Fact]
    public async Task Verification_is_repeated_once_the_window_expires()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var compiler = new CountingCompilerProcess();
        var readiness = Create(compiler, clock, success: TimeSpan.FromMinutes(5));

        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(1, compiler.Compilations);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(2, compiler.Compilations);
    }

    [Fact]
    public async Task A_failing_compiler_reports_unready_and_is_rechecked_sooner_than_a_success()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var compiler = new CountingCompilerProcess { Fail = true };
        var readiness = Create(
            compiler,
            clock,
            success: TimeSpan.FromMinutes(5),
            failure: TimeSpan.FromSeconds(15));

        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(1, compiler.Compilations);

        // Still inside the short failure window: cached, no new worker.
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(1, compiler.Compilations);

        // Past it, recovery is noticed without waiting out a success-length window.
        clock.Advance(TimeSpan.FromSeconds(11));
        compiler.Fail = false;
        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(2, compiler.Compilations);
    }

    [Fact]
    public async Task Concurrent_probes_on_a_cold_cache_verify_once()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var compiler = new CountingCompilerProcess { Delay = TimeSpan.FromMilliseconds(150) };
        var readiness = Create(compiler, clock, success: TimeSpan.FromMinutes(5));

        var probes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => readiness.IsReadyAsync(CancellationToken.None).AsTask()));

        Assert.All(probes, Assert.True);
        // Single-flight: eight simultaneous probes must not mean eight worker processes.
        Assert.Equal(1, compiler.Compilations);
    }

    private static LinqCompilerReadiness Create(
        CountingCompilerProcess compiler,
        TimeProvider clock,
        TimeSpan success,
        TimeSpan? failure = null)
        => new(
            compiler,
            Options.Create(new LinqCompilerOptions
            {
                ReadinessCacheDuration = success,
                ReadinessFailureCacheDuration = failure ?? TimeSpan.FromSeconds(15),
            }),
            clock);

    private sealed class CountingCompilerProcess : ILinqCompilerProcess
    {
        private int _compilations;

        public int Compilations => Volatile.Read(ref _compilations);

        public bool Fail { get; set; }

        public TimeSpan Delay { get; set; }

        public async Task<QueryPlan> CompileAsync(
            QueryPlanningRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _compilations);
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            if (Fail)
            {
                throw new InvalidOperationException("The LINQ compiler worker failed.");
            }

            return new QueryPlan("SELECT 1", [], [], "schema-1");
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}

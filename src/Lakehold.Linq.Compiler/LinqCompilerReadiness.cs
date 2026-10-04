using Microsoft.Extensions.Options;
using Lakehold.Querying;

namespace Lakehold.Linq.Compiler;

/// <summary>
/// Caches the outcome of compiler readiness verification. Verification stays real — one
/// compilation proves the Roslyn toolchain and worker transport are usable — but it is amortised
/// over a cache window instead of repeated on every probe.
/// </summary>
public sealed class LinqCompilerReadiness(
    ILinqCompilerProcess compiler,
    IOptions<LinqCompilerOptions> options,
    TimeProvider clock) : IDisposable
{
    private static readonly QueryPlanningRequest Probe = new(
        "Main.Readiness.Take(1)",
        "readiness",
        [new QueryTableSchema(
            "main",
            "readiness",
            "TABLE",
            [new QueryColumnSchema("value", "INTEGER", false)])]);

    private readonly LinqCompilerOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Outcome? _outcome;

    /// <summary>Reports whether the compiler is usable, verifying at most once per cache window.</summary>
    public async ValueTask<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (TryReadFresh(out var cached))
        {
            return cached;
        }

        // Single-flight: a stale cache must produce one verification, not one per concurrent probe.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryReadFresh(out cached))
            {
                return cached;
            }

            bool ready;
            try
            {
                // Deliberately not the request token. The outcome is shared singleton state and
                // CompileAsync already bounds itself with the configured hard timeout, so a probe
                // that gives up waiting must not discard a near-complete verification and force the
                // next probe to spawn another worker.
                _ = await compiler.CompileAsync(Probe, CancellationToken.None).ConfigureAwait(false);
                ready = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ready = false;
            }

            var window = ready ? _options.ReadinessCacheDuration : _options.ReadinessFailureCacheDuration;
            Volatile.Write(ref _outcome, new Outcome(ready, clock.GetUtcNow() + window));
            return ready;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private bool TryReadFresh(out bool ready)
    {
        // One volatile reference read keeps the flag and its expiry consistent with each other.
        var outcome = Volatile.Read(ref _outcome);
        if (outcome is not null && clock.GetUtcNow() < outcome.Expires)
        {
            ready = outcome.Ready;
            return true;
        }

        ready = false;
        return false;
    }

    private sealed record Outcome(bool Ready, DateTimeOffset Expires);
}

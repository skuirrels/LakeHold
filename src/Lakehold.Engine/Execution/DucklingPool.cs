using System.Collections.Concurrent;
using DuckDB.EFCoreProvider.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Telemetry;

namespace Lakehold.Engine.Execution;

/// <summary>
///     Keeps one warm <see cref="Duckling"/> per tenant catalog, creating them on demand and
///     evicting them once idle.
/// </summary>
/// <remarks>
///     This is the hypertenancy primitive: tenants get a dedicated compute session rather than a
///     slot on a shared cluster, so one tenant's workload cannot degrade another's. Because the
///     durable state lives in DuckLake rather than in the session, evicting a session costs only
///     the next query's start-up, which is why an aggressive idle timeout is safe.
/// </remarks>
public sealed class DucklingPool : IAsyncDisposable
{
    // The idle sweep is throttled to at most once per this interval, capped below the idle timeout so
    // a short timeout still evicts promptly. Bounds the sweep's cost under load without letting an
    // aged-out session linger meaningfully longer than the timeout it configured.
    private static readonly TimeSpan SweepFloor = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Lazy<Task<Duckling>>> _sessions = new(StringComparer.Ordinal);
    private readonly LakehouseOptions _options;
    private readonly ILogger<DucklingPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _sweepInterval;
    private long _lastSweepTicks;
    private bool _disposed;

    public DucklingPool(
        IOptions<LakehouseOptions> options,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DucklingPool>();
        _sweepInterval = _options.IdleTimeout < SweepFloor ? _options.IdleTimeout : SweepFloor;
    }

    /// <summary>
    ///     Returns the warm session for <paramref name="catalog"/>, starting one if needed.
    /// </summary>
    /// <remarks>
    ///     Concurrent callers for the same catalog share one start-up via <see cref="Lazy{T}"/>,
    ///     so a burst of simultaneous first-queries opens one DuckDB instance rather than N.
    /// </remarks>
    public async Task<Duckling> GetOrStartAsync(
        CatalogDescriptor catalog,
        Action<DuckDBDbContextOptionsBuilder>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EvictIdleAsync().ConfigureAwait(false);

        // Recorded before GetOrAdd so a miss is attributed to the caller that actually pays the
        // start-up, rather than to whichever request happened to arrive while it was still starting.
        var hit = _sessions.ContainsKey(catalog.CatalogName);
        LakeholdTelemetry.PoolRequests.Add(
            1,
            new KeyValuePair<string, object?>(
                LakeholdTelemetry.ResultKey,
                hit ? LakeholdTelemetry.ResultHit : LakeholdTelemetry.ResultMiss));

        var entry = _sessions.GetOrAdd(
            catalog.CatalogName,
            _ => new Lazy<Task<Duckling>>(
                () => Duckling.StartAsync(
                    catalog,
                    _options,
                    configure,
                    _loggerFactory.CreateLogger<Duckling>(),
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed start-up must not be cached, or every later request for this catalog would
            // replay the same failure without ever retrying the connection.
            _sessions.TryRemove(new KeyValuePair<string, Lazy<Task<Duckling>>>(catalog.CatalogName, entry));
            EngineLog.DucklingStartFailed(_logger, ex, catalog.CatalogName);
            throw;
        }
    }

    /// <summary>
    ///     Evicts the session for a catalog, if one is warm. Call after a catalog's configuration
    ///     changes so the next query reattaches with the new settings.
    /// </summary>
    /// <remarks>
    ///     Callers that resolve catalogs through the control plane must also drop the cached
    ///     descriptor, or the replacement session is started from the stale configuration this
    ///     eviction was meant to discard. <c>LakehouseService.ForgetCatalogAsync</c> pairs the two.
    /// </remarks>
    public async Task EvictAsync(string catalogName)
    {
        if (_sessions.TryRemove(catalogName, out var entry))
        {
            LakeholdTelemetry.SessionEvictions.Add(
                1, new KeyValuePair<string, object?>(LakeholdTelemetry.ReasonKey, "explicit"));
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>Currently warm catalog names.</summary>
    public IReadOnlyCollection<string> WarmCatalogs => _sessions.Keys.ToArray();

    private async Task EvictIdleAsync()
    {
        var now = DateTimeOffset.UtcNow;

        // Skip the scan while a recent sweep still covers us: it allocates a candidate list and walks
        // every warm session, which is wasted work on the query hot path when nothing has aged out.
        // Never skip when already over the ceiling, so an overflowing pool is trimmed on the same
        // request that pushed it over rather than one sweep interval later.
        var last = Interlocked.Read(ref _lastSweepTicks);
        var overflowing = _sessions.Count > _options.MaxWarmSessions;

        if (now.UtcTicks - last < _sweepInterval.Ticks && !overflowing)
        {
            return;
        }

        // Claim the sweep window. Without this, every caller that arrived after the interval elapsed
        // would pass the check above before any of them stamped, and all of them would sweep — a
        // thundering herd that reinstates per-query the allocation this throttle exists to remove.
        // Losing the race means another thread is already sweeping, so there is nothing left to do
        // unless we are over the ceiling, where trimming promptly outweighs a duplicated pass.
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last && !overflowing)
        {
            return;
        }

        var candidates = new List<(string Name, Lazy<Task<Duckling>> Entry, DateTimeOffset LastUsed)>();

        foreach (var (name, entry) in _sessions)
        {
            // Skip sessions still starting up — they have no meaningful idle time yet.
            if (!entry.IsValueCreated || !entry.Value.IsCompletedSuccessfully)
            {
                continue;
            }

            candidates.Add((name, entry, entry.Value.Result.LastUsedUtc));
        }

        foreach (var (name, entry, lastUsed) in candidates)
        {
            if (now - lastUsed > _options.IdleTimeout && _sessions.TryRemove(name, out var removed))
            {
                LakeholdTelemetry.SessionEvictions.Add(
                    1, new KeyValuePair<string, object?>(LakeholdTelemetry.ReasonKey, "idle"));
                EngineLog.DucklingEvictedIdle(_logger, name);
                await DisposeEntryAsync(removed).ConfigureAwait(false);
            }
        }

        // Over the warm-session ceiling, evict least-recently-used until back within budget.
        var overflow = _sessions.Count - _options.MaxWarmSessions;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var (name, _, _) in candidates.OrderBy(c => c.LastUsed).Take(overflow))
        {
            if (_sessions.TryRemove(name, out var removed))
            {
                LakeholdTelemetry.SessionEvictions.Add(
                    1, new KeyValuePair<string, object?>(LakeholdTelemetry.ReasonKey, "overflow"));
                EngineLog.DucklingEvictedOverflow(_logger, name);
                await DisposeEntryAsync(removed).ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposeEntryAsync(Lazy<Task<Duckling>> entry)
    {
        if (!entry.IsValueCreated)
        {
            return;
        }

        try
        {
            var duckling = await entry.Value.ConfigureAwait(false);
            await duckling.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A session that failed to start has nothing to dispose. The start-up failure was
            // already logged and surfaced to the caller that triggered it.
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var entry in _sessions.Values)
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }

        _sessions.Clear();
    }
}

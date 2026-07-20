using System.Collections.Concurrent;
using DuckDB.EFCoreProvider.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;

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
    private readonly ConcurrentDictionary<string, Lazy<Task<Duckling>>> _sessions = new(StringComparer.Ordinal);
    private readonly LakehouseOptions _options;
    private readonly ILogger<DucklingPool> _logger;
    private readonly ILoggerFactory _loggerFactory;
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
    public async Task EvictAsync(string catalogName)
    {
        if (_sessions.TryRemove(catalogName, out var entry))
        {
            await DisposeEntryAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>Currently warm catalog names.</summary>
    public IReadOnlyCollection<string> WarmCatalogs => _sessions.Keys.ToArray();

    private async Task EvictIdleAsync()
    {
        var now = DateTimeOffset.UtcNow;
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

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using DuckDB.EFCoreProvider.Extensions;
using DuckDB.EFCoreProvider.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Telemetry;

namespace Lakehold.Engine.Execution;

/// <summary>
///     One tenant's compute session: a DuckDB instance with that tenant's DuckLake catalog attached
///     and selected, hosted by an EF Core <see cref="LakeContext"/>.
/// </summary>
/// <remarks>
///     <para>
///         A Duckling is the unit of isolation. Tenants never share one, so a runaway query consumes
///         only its own memory limit and thread budget, and a tenant can only reference the catalogs
///         attached to its own session. Isolation is enforced by what is attached, not by filtering
///         the SQL a tenant submits.
///     </para>
///     <para>
///         DuckDB is single-writer per instance and <see cref="DbContext"/> is not thread-safe, so
///         access is serialised with a semaphore. Those two constraints coincide, so the gate costs
///         nothing that DuckDB was not already imposing. Concurrent readers scale by attaching
///         read-only replicas, which DuckLake's snapshot isolation makes safe.
///     </para>
/// </remarks>
public sealed class Duckling : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LakeContext _context;
    private readonly LakehouseOptions _options;
    private readonly ILogger _logger;
    private bool _disposed;

    private Duckling(LakeContext context, CatalogDescriptor catalog, LakehouseOptions options, ILogger logger)
    {
        _context = context;
        _options = options;
        _logger = logger;
        Catalog = catalog;
        LastUsedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>The catalog attached to this session.</summary>
    public CatalogDescriptor Catalog { get; }

    /// <summary>When this session last executed a statement. Drives idle eviction.</summary>
    public DateTimeOffset LastUsedUtc { get; private set; }

    /// <summary>
    ///     Access to the provider's DuckLake maintenance surface for this session's catalog.
    /// </summary>
    internal DuckLakeMaintenanceFacade Maintenance => _context.Database.DuckLake();

    /// <summary>
    ///     Builds a session for <paramref name="catalog"/>: applies resource limits, loads
    ///     extensions, then attaches and selects the DuckLake catalog.
    /// </summary>
    /// <remarks>
    ///     The provider owns the ordering — extensions, secret callback, <c>ATTACH</c>, <c>USE</c> —
    ///     which is fiddly enough that reimplementing it was the main source of start-up bugs before
    ///     1.13.0. The DuckDB instance holds no tenant data: all durable state lives in the DuckLake
    ///     metadata catalog and the Parquet files under its data path, which is what makes a session
    ///     cheap to create and safe to evict.
    /// </remarks>
    /// <param name="catalog">The catalog to attach.</param>
    /// <param name="options">Node-wide session limits.</param>
    /// <param name="configure">
    ///     Optional extra provider configuration, applied before the catalog is attached. This is
    ///     where an object-store secret belongs: create it in <c>ConfigureConnection</c> so the
    ///     credential lives only in the session and never reaches the catalog, logs, or options.
    /// </param>
    /// <param name="logger">Session lifecycle logger.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<Duckling> StartAsync(
        CatalogDescriptor catalog,
        LakehouseOptions options,
        Action<DuckDBDbContextOptionsBuilder>? configure,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Validate before building anything: the catalog name reaches ATTACH, which cannot be
        // parameterised, so an invalid descriptor must fail before a connection is opened.
        _ = SqlIdentifier.Quote(catalog.CatalogName, nameof(catalog));

        var builder = new DbContextOptionsBuilder<LakeContext>();
        builder.UseDuckDB(
            "Data Source=:memory:",
            duckDb =>
            {
                duckDb.MemoryLimit(options.MemoryLimit);

                foreach (var extension in options.Extensions)
                {
                    duckDb.LoadExtension(SqlIdentifier.ValidateExtension(extension));
                }

                // The provider has no thread-count setter, and threads must be set before the
                // catalog is attached, so it goes through the connection initializer.
                duckDb.ConfigureConnection(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SET threads = {options.Threads.ToString(CultureInfo.InvariantCulture)}";
                    command.ExecuteNonQuery();
                });

                configure?.Invoke(duckDb);

                void ConfigureLake(DuckLakeDbContextOptionsBuilder lake)
                {
                    lake.CatalogName(catalog.CatalogName);
                    lake.DataPath(catalog.DataPath, overrideForCurrentConnection: false);
                    lake.ReadOnly(catalog.ReadOnly);

                    // Read-only shares and reference catalogs mounted alongside the tenant's own,
                    // enabling cross-catalog joins without widening write access.
                    foreach (var extra in catalog.AdditionalCatalogs)
                    {
                        lake.AlsoAttach(extra.CatalogName, extra.MetadataSource, readOnly: true);
                    }
                }

                if (catalog.MetadataKind == CatalogMetadataKind.LocalFile)
                {
                    duckDb.UseDuckLake(catalog.MetadataSource, ConfigureLake);
                    return;
                }

                // A remote metadata catalog is addressed by secret, never by connection string: the
                // provider rejects a non-file metadata path outright, and a DuckLake profile secret
                // is where the credential is meant to live. The secret itself is created by the
                // caller in ConfigureConnection, so it never reaches this record or a log.
                duckDb.UseDuckLake(lake =>
                {
                    lake.UseNamedSecret(catalog.MetadataSource);
                    ConfigureLake(lake);
                });
            });

        // A cold start is the pool's whole reason to exist, so it is measured on its own rather than
        // buried inside the first query that happened to pay for it.
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.session.start");
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalog.CatalogName);
        activity?.SetTag(LakeholdTelemetry.MetadataKindKey, catalog.MetadataKind.ToString());
        var startedAt = TimeProvider.System.GetTimestamp();

        var context = new LakeContext(builder.Options);
        try
        {
            // Force connection initialisation now rather than on the first user query, so a
            // misconfigured catalog surfaces as a start-up failure the pool can refuse to cache.
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            LakeholdTelemetry.SessionStartDuration.Record(
                TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
                new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, LakeholdTelemetry.OutcomeSuccess));
            LakeholdTelemetry.WarmSessions.Add(1);

            EngineLog.DucklingStarted(logger, catalog.CatalogName, catalog.MetadataKind, catalog.ReadOnly);
            return new Duckling(context, catalog, options, logger);
        }
        catch (Exception ex)
        {
            LakeholdTelemetry.SessionStartDuration.Record(
                TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
                new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, LakeholdTelemetry.OutcomeError));
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);

            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Executes a statement and materialises up to
    ///     <see cref="LakehouseOptions.MaxRowsPerResult"/> rows.
    /// </summary>
    public async Task<QueryResult> ExecuteQueryAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StatementTimeout);
        var token = timeout.Token;

        await WaitForGateAsync(token).ConfigureAwait(false);
        try
        {
            return await ExecuteUnguardedAsync(sql, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Takes the session gate, recording how long the caller queued for it.
    /// </summary>
    /// <remarks>
    ///     Queue time is measured separately from execution because the two have opposite remedies:
    ///     a slow statement wants a better query or more memory, while a queued one wants a node of
    ///     its own or a read replica. Summed into request duration they are indistinguishable.
    /// </remarks>
    private async Task WaitForGateAsync(CancellationToken cancellationToken)
    {
        var queuedAt = TimeProvider.System.GetTimestamp();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LakeholdTelemetry.SessionQueueDuration.Record(
            TimeProvider.System.GetElapsedTime(queuedAt).TotalSeconds);
    }

    /// <summary>
    ///     Executes a statement <em>without</em> taking the session gate.
    /// </summary>
    /// <remarks>
    ///     The caller must already hold the gate, normally by running inside
    ///     <see cref="InvokeAsync{T}"/>. The gate is a non-reentrant <see cref="SemaphoreSlim"/>, so
    ///     a multi-statement operation that reached for <see cref="ExecuteQueryAsync"/> would
    ///     deadlock against itself rather than fail — which is exactly what catalog backup did on
    ///     its first run.
    /// </remarks>
    internal async Task<QueryResult> ExecuteUnguardedAsync(string sql, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StatementTimeout);
        var token = timeout.Token;

        LastUsedUtc = DateTimeOffset.UtcNow;
        var startedAt = TimeProvider.System.GetTimestamp();

        await using var dynamic = await _context.Database
            .SqlQueryDynamicRawAsync(sql, token)
            .ConfigureAwait(false);

        var columns = dynamic.Columns
            .Select(c => new ResultColumn(c.Name, c.DuckDBTypeName, c.ClrType.Name))
            .ToArray();

        var rows = new List<object?[]>();
        var truncated = false;

        // The provider streams rows, so stopping at the ceiling stops the scan rather than
        // discarding an already-materialised result.
        await foreach (var row in dynamic.ReadRowsAsync(token).ConfigureAwait(false))
        {
            if (rows.Count >= _options.MaxRowsPerResult)
            {
                truncated = true;
                break;
            }

            var values = new object?[row.Length];
            var span = row.Span;
            for (var i = 0; i < span.Length; i++)
            {
                values[i] = ToWireValue(span[i]);
            }

            rows.Add(values);
        }

        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            Truncated = truncated,
            Elapsed = TimeProvider.System.GetElapsedTime(startedAt),
        };
    }

    /// <summary>Runs <paramref name="action"/> under this session's exclusive gate.</summary>
    /// <remarks>
    ///     Maintenance shares the gate with queries because both run on the same non-thread-safe
    ///     context, and because compaction rewriting files under a concurrent scan is exactly the
    ///     race worth excluding.
    /// </remarks>
    internal async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastUsedUtc = DateTimeOffset.UtcNow;
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The largest integer a JavaScript number represents exactly: 2^53 - 1.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    private static bool IsJsSafe(long value) => value is >= -MaxSafeInteger and <= MaxSafeInteger;

    /// <summary>
    ///     Projects a provider-materialised value into a shape that survives JSON serialisation.
    /// </summary>
    /// <remarks>
    ///     The provider already handles DuckDB-to-CLR mapping, nested values, and null
    ///     normalisation, so this is no longer type mapping — it is purely a wire-format concern.
    ///     JSON numbers are IEEE-754 doubles, so any integer past 2^53 would be silently rounded by
    ///     the browser's parser; wide integers and decimals therefore cross the wire as strings and
    ///     are formatted client-side.
    /// </remarks>
    private static object? ToWireValue(object? value) => value switch
    {
        null => null,

        // An integer within JavaScript's safe range crosses the wire as a JSON number: the browser
        // parses it losslessly, so stringifying it would only spend an allocation the grid discards.
        // Past 2^53 a JSON number would be silently rounded, so those — and every decimal, whose
        // precision a double cannot hold, and every BigInteger, which is wide by definition — are
        // transported as strings and formatted client-side. Returning the boxed value unchanged for
        // the common small-integer case reuses the box the provider already allocated.
        long l => IsJsSafe(l) ? value : l.ToString(CultureInfo.InvariantCulture),
        ulong ul => ul <= (ulong)MaxSafeInteger ? value : ul.ToString(CultureInfo.InvariantCulture),
        decimal or BigInteger => Convert.ToString(value, CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToBase64String(bytes),

        // LIST, STRUCT, and MAP arrive as CLR collections. Project them recursively so the grid
        // renders values rather than type names.
        IDictionary dictionary => ToWireDictionary(dictionary),
        IEnumerable sequence and not string => sequence.Cast<object?>().Select(ToWireValue).ToArray(),

        _ => value,
    };

    private static Dictionary<string, object?> ToWireDictionary(IDictionary dictionary)
    {
        var projected = new Dictionary<string, object?>(dictionary.Count, StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
            projected[key] = ToWireValue(entry.Value);
        }

        return projected;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _context.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();

        // Paired with the increment on a successful start, so the gauge tracks live sessions however
        // they end — idle eviction, overflow, explicit eviction, or node shutdown.
        LakeholdTelemetry.WarmSessions.Add(-1);
        EngineLog.DucklingStopped(_logger, Catalog.CatalogName);
    }
}

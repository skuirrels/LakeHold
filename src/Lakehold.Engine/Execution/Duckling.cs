using System.Collections;
using System.Data;
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

                // Both resource limits are set the same way as of provider 1.14.0. This used to run
                // `SET threads` through ConfigureConnection, which also meant competing with the
                // caller's configure callback for the same hook — the one an object-store secret
                // needs.
                duckDb.Threads(options.Threads);

                foreach (var extension in options.Extensions)
                {
                    duckDb.LoadExtension(SqlIdentifier.ValidateExtension(extension));
                }

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
                        // Read-only in both branches, and the provider enforces it: writing through
                        // a share fails in the engine, not only by convention here.
                        if (extra.MetadataKind == CatalogMetadataKind.LocalFile)
                        {
                            lake.AlsoAttach(extra.CatalogName, extra.MetadataSource, readOnly: true);
                        }
                        else
                        {
                            lake.AlsoAttachNamedSecret(extra.CatalogName, extra.MetadataSource, readOnly: true);
                        }
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
    ///     <see cref="LakehouseOptions.MaxRowsPerResult"/> rows, or — for a statement whose outcome
    ///     is a count rather than rows — reports the number of rows it changed.
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
            return StatementVerb.ReportsAffectedRows(sql)
                ? await ExecuteNonQueryUnguardedAsync(sql, token).ConfigureAwait(false)
                : await ExecuteUnguardedAsync(sql, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Executes a statement and hands each row to <paramref name="onRow"/> as the provider
    ///     yields it, without materialising the result.
    /// </summary>
    /// <param name="sql">The statement to execute.</param>
    /// <param name="onColumns">
    ///     Invoked once, before the first row, with the result's column schema. A wire protocol has
    ///     to describe the row shape before it can send rows, so this cannot be deferred to the end.
    /// </param>
    /// <param name="onRow">
    ///     Invoked per row. The span is the provider's own buffer and is only valid for the duration
    ///     of the call — a consumer that needs to retain a row must copy it.
    /// </param>
    /// <param name="maxRows">Row ceiling, or zero for unbounded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows handed to <paramref name="onRow"/>.</returns>
    /// <remarks>
    ///     <para>
    ///         This deliberately does not apply <see cref="LakehouseOptions.MaxRowsPerResult"/>. That
    ///         ceiling exists because <see cref="ExecuteUnguardedAsync"/> builds a list that a JSON
    ///         response is serialised from, so an unbounded result would be held in memory in full
    ///         before any of it was sent. Streaming has no such moment: a row is encoded, written,
    ///         and forgotten. The invariant's purpose — never materialise an unbounded result — is
    ///         honoured here by construction rather than by truncation.
    ///     </para>
    ///     <para>
    ///         Truncating instead would be the worse failure. A caller that receives a silent prefix
    ///         of a result reports a confidently wrong number, whereas a slow query is merely slow.
    ///         Callers that want a ceiling anyway pass one; the statement timeout, cancellation, and
    ///         early termination all behave exactly as they do on the materialising path.
    ///     </para>
    /// </remarks>
    public async Task<long> StreamQueryAsync(
        string sql,
        Func<IReadOnlyList<StreamColumn>, CancellationToken, Task> onColumns,
        RowHandler onRow,
        int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(onColumns);
        ArgumentNullException.ThrowIfNull(onRow);
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StatementTimeout);
        var token = timeout.Token;

        await WaitForGateAsync(token).ConfigureAwait(false);
        try
        {
            LastUsedUtc = DateTimeOffset.UtcNow;

            await using var dynamic = await _context.Database
                .SqlQueryDynamicRawAsync(sql, token)
                .ConfigureAwait(false);

            var columns = dynamic.Columns
                .Select(c => new StreamColumn(c.Name, c.DuckDBTypeName, c.ClrType))
                .ToArray();

            await onColumns(columns, token).ConfigureAwait(false);

            var count = 0L;
            await foreach (var row in dynamic.ReadRowsAsync(token).ConfigureAwait(false))
            {
                if (maxRows > 0 && count >= maxRows)
                {
                    break;
                }

                await onRow(row, token).ConfigureAwait(false);
                count++;
            }

            // Stamped again on the way out. A stream can run for as long as the statement timeout
            // allows, and leaving the timestamp at the moment the scan started would have the pool
            // judge a session that has been busy throughout as though it had been idle that whole
            // time — which, on a deployment whose idle timeout is shorter than its statement
            // timeout, is an eviction of a session still in use.
            LastUsedUtc = DateTimeOffset.UtcNow;
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Receives one streamed row. Declared as a named delegate because a
    ///     <see cref="ReadOnlyMemory{T}"/> parameter cannot be expressed through
    ///     <see cref="Func{T1, T2, TResult}"/> without boxing the row into an array first, which is
    ///     the allocation streaming exists to avoid.
    /// </summary>
    public delegate Task RowHandler(ReadOnlyMemory<object?> row, CancellationToken cancellationToken);

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

    /// <summary>
    ///     Executes a statement as a non-query and reports how many rows it changed, without taking
    ///     the session gate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The provider's dynamic path cannot report this. DuckDB.NET's data reader exposes
    ///         <c>RecordsAffected == -1</c>, so a DML statement run through
    ///         <c>SqlQueryDynamicRawAsync</c> comes back with no columns and no rows — identical to a
    ///         statement that returned nothing — and every successful insert was reported as
    ///         "0 rows". <c>ExecuteNonQuery</c> on the same connection does report the count, so the
    ///         count is reachable; it is only the dynamic API that lacks it.
    ///     </para>
    ///     <para>
    ///         The command is built on the context's own connection rather than sent through
    ///         <c>ExecuteSqlRawAsync</c>, and that is not a stylistic preference. EF parses its raw
    ///         SQL as a composite format string, so a brace anywhere in the statement is read as a
    ///         placeholder: <c>INSERT INTO s VALUES (1, {'a': 1})</c> — an ordinary DuckDB struct
    ///         literal — fails with <c>FormatException</c> before it reaches the engine. Doubling the
    ///         braces would work today and corrupt the statement the moment EF stopped formatting.
    ///         This is EF's own connection and EF's own command, not a second connection stack.
    ///     </para>
    /// </remarks>
    private async Task<QueryResult> ExecuteNonQueryUnguardedAsync(string sql, CancellationToken cancellationToken)
    {
        LastUsedUtc = DateTimeOffset.UtcNow;
        var startedAt = TimeProvider.System.GetTimestamp();

        var connection = _context.Database.GetDbConnection();
        if (connection.State is not ConnectionState.Open)
        {
            // Sessions open their connection at start-up and hold it, so this is a repair rather
            // than a normal path. It is never closed here: closing would drop the attached catalog.
            await _context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        LastUsedUtc = DateTimeOffset.UtcNow;

        return new QueryResult
        {
            Columns = [],
            Rows = [],
            Truncated = false,
            RowsAffected = affected,
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

    /// <summary>
    ///     Runs <paramref name="action"/> under this session's exclusive gate, inside a transaction
    ///     whose DuckLake snapshot is labelled with <paramref name="commitMessage"/>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A snapshot list is only self-documenting if the entries say what made them. Everything
    ///         Lakehold commits on its own initiative — flush, compaction — used to land as an
    ///         unlabelled snapshot beside the tenant's own writes, so the history could show what
    ///         changed but never why.
    ///     </para>
    ///     <para>
    ///         The message belongs to one transaction, which is why it is set here rather than held
    ///         as session state: the provider refuses the call outside a transaction precisely so a
    ///         message cannot leak onto a later, unrelated write. An operation that changes nothing
    ///         commits no snapshot, so a no-op maintenance run leaves no labelled entry behind.
    ///     </para>
    /// </remarks>
    internal async Task<T> InvokeLabelledAsync<T>(
        string commitMessage,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await WaitForGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastUsedUtc = DateTimeOffset.UtcNow;

            await using var transaction = await _context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var result = await action(cancellationToken).ConfigureAwait(false);

            await Maintenance
                .SetCommitMessageAsync(CommitAuthor, commitMessage, extraInfo: null, cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Author recorded on snapshots Lakehold commits on its own initiative, so maintenance is
    ///     distinguishable from a tenant's own writes in the snapshot list.
    /// </summary>
    private const string CommitAuthor = "lakehold";

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

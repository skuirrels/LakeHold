using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Engine.Telemetry;
using Microsoft.Extensions.Options;
using Lakehold.Engine.Execution;

namespace Lakehold.ControlPlane.Data;

/// <summary>Raised when a tenant or catalog does not exist, or is not visible to the caller.</summary>
public sealed class CatalogNotFoundException(string message) : Exception(message);

/// <summary>
///     The seam between the control plane and the data plane: resolves a tenant's catalog, runs a
///     statement on that tenant's compute session, and records the outcome.
/// </summary>
public sealed class LakehouseService(
    ControlPlaneContext context,
    DucklingPool pool,
    IOptions<LakehouseOptions> options)
{
    private readonly ControlPlaneContext _context = context;
    private readonly DucklingPool _pool = pool;
    private readonly LakehouseOptions _options = options.Value;

    /// <summary>
    ///     Executes <paramref name="sql"/> against a tenant's catalog and records the run.
    /// </summary>
    /// <remarks>
    ///     Tenant isolation comes from resolving the catalog through the tenant's own record and
    ///     attaching only that catalog to the session. The SQL itself is never inspected for
    ///     cross-tenant references, because a tenant's session has no other catalog attached to
    ///     reference.
    /// </remarks>
    public async Task<QueryResult> ExecuteAsync(
        string tenantSlug,
        string catalogName,
        string sql,
        CancellationToken cancellationToken,
        bool readOnly = false,
        int? tokenId = null,
        bool recordHistory = true,
        IReadOnlyList<NamedQueryParameter>? parameters = null,
        string language = "sql",
        string? source = null)
    {
        // The span carries tenant and catalog; the metrics deliberately do not. Per-tenant time
        // series would blow a metrics backend's cardinality budget on a multi-tenant node, and a slow
        // tenant is still findable through the trace.
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.query");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken, readOnly).ConfigureAwait(false);

        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = source ?? sql,
            Language = language,
            StartedUtc = DateTimeOffset.UtcNow,
            TokenId = tokenId,
        };

        try
        {
            var result = await duckling.ExecuteQueryAsync(sql, parameters ?? [], cancellationToken).ConfigureAwait(false);

            run.Succeeded = true;
            run.RowCount = result.Rows.Count;
            run.ElapsedMilliseconds = result.Elapsed.TotalMilliseconds;

            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeSuccess);
            activity?.SetTag(LakeholdTelemetry.RowsKey, result.Rows.Count);
            activity?.SetTag(LakeholdTelemetry.TruncatedKey, result.Truncated);

            if (result.RowsAffected is { } affected)
            {
                activity?.SetTag(LakeholdTelemetry.RowsAffectedKey, affected);
            }

            LakeholdTelemetry.QueryRows.Record(result.Rows.Count);

            if (result.Truncated)
            {
                LakeholdTelemetry.QueriesTruncated.Add(1);
            }

            return result;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;

            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            if (recordHistory)
            {
                await SaveQueryRunAsync(run).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Imports an uploaded CSV, XLSX, or Avro scratch file into a new table and records the operation.</summary>
    public async Task<TabularImportResult> ImportTabularAsync(
        string tenantSlug,
        string catalogName,
        string filePath,
        string fileName,
        TabularFileFormat format,
        string schema,
        string table,
        bool automaticMode,
        CsvReadOptions csvOptions,
        string? worksheet,
        CancellationToken cancellationToken,
        int? tokenId = null)
    {
        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.tabular.import");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        activity?.SetTag("lakehold.import.format", format.ToString().ToLowerInvariant());
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false);
        var auditFileName = string.Concat(
            Path.GetFileName(fileName).Select(character => char.IsControl(character) ? ' ' : character));
        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            // The real node-local path is deliberately absent from durable history.
            Sql = $"-- Browser {format.ToString().ToUpperInvariant()} import: {auditFileName}\n"
                  + $"CREATE TABLE {SqlIdentifier.QuoteName(validatedSchema)}."
                  + $"{SqlIdentifier.QuoteName(validatedTable)} "
                  + $"AS SELECT * FROM read_{format.ToString().ToLowerInvariant()}('<uploaded file>');",
            StartedUtc = DateTimeOffset.UtcNow,
            TokenId = tokenId,
        };

        try
        {
            TabularImportResult result;
            if (format == TabularFileFormat.Csv)
            {
                try
                {
                    result = await TabularImporter
                        .ImportCsvAsync(
                            duckling,
                            filePath,
                            fileName,
                            validatedSchema,
                            validatedTable,
                            csvOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (CsvImportException ex) when (automaticMode && ex.IsParserError)
                {
                    // DuckDB aborts a transaction after a parser error. ImportCsvAsync has already
                    // rolled that transaction back, so automatic recovery starts a fresh labelled
                    // transaction against the same request-scoped scratch file.
                    result = await TabularImporter
                        .ImportCsvAsync(
                            duckling,
                            filePath,
                            fileName,
                            validatedSchema,
                            validatedTable,
                            new CsvReadOptions(
                                SampleSize: -1,
                                IgnoreErrors: true,
                                StoreRejects: true),
                            cancellationToken)
                        .ConfigureAwait(false);
                    result = result with { UsedAutomaticFallback = true };
                    run.Sql += "\n-- Automatic recovery skipped malformed rows and captured rejects.";
                    activity?.SetTag("lakehold.import.automatic_fallback", true);
                }
            }
            else if (format == TabularFileFormat.Xlsx)
            {
                result = await TabularImporter
                    .ImportXlsxAsync(
                        duckling,
                        filePath,
                        fileName,
                        validatedSchema,
                        validatedTable,
                        worksheet,
                        cancellationToken)
                        .ConfigureAwait(false);
            }
            else
            {
                result = await TabularImporter
                    .ImportAvroAsync(
                        duckling,
                        filePath,
                        fileName,
                        validatedSchema,
                        validatedTable,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            run.Succeeded = true;
            run.RowCount = (int)Math.Min(result.RowsImported, int.MaxValue);
            run.ElapsedMilliseconds = result.Elapsed.TotalMilliseconds;

            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeSuccess);
            activity?.SetTag(LakeholdTelemetry.RowsKey, result.RowsImported);
            LakeholdTelemetry.QueryRows.Record(result.RowsImported);
            return result;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            await SaveQueryRunAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Publishes a managed connector's full JSON snapshot after validating its declared data
    ///     contract. The disposable source path is deliberately excluded from durable history.
    /// </summary>
    public async Task<JsonSnapshotImportResult> ReplaceJsonSnapshotAsync(
        string tenantSlug,
        string catalogName,
        string connectorName,
        int connectorId,
        string filePath,
        string schema,
        string table,
        bool replaceExistingTarget,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.connector.publish");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        activity?.SetTag("lakehold.connector.name", connectorName);
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false);
        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = $"-- Managed connector refresh: {connectorName}\n"
                  + $"CREATE OR REPLACE TABLE {SqlIdentifier.QuoteName(validatedSchema)}."
                  + $"{SqlIdentifier.QuoteName(validatedTable)} AS SELECT * FROM '<connector snapshot>';",
            StartedUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            var result = await JsonSnapshotImporter.ReplaceAsync(
                    duckling,
                    filePath,
                    validatedSchema,
                    validatedTable,
                    replaceExistingTarget,
                    quality,
                    schemaBehavior,
                    $"lakehold.connector:{connectorId}",
                    cancellationToken)
                .ConfigureAwait(false);
            run.Succeeded = true;
            run.RowCount = (int)Math.Min(result.RowsPublished, int.MaxValue);
            run.ElapsedMilliseconds = result.Elapsed.TotalMilliseconds;
            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeSuccess);
            activity?.SetTag(LakeholdTelemetry.RowsKey, result.RowsPublished);
            LakeholdTelemetry.QueryRows.Record(result.RowsPublished);
            return result;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            await SaveQueryRunAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>Atomically applies a connector delta by key so a replay cannot duplicate rows.</summary>
    public async Task<JsonSnapshotImportResult> UpsertJsonDeltaAsync(
        string tenantSlug,
        string catalogName,
        string connectorName,
        int connectorId,
        string filePath,
        string schema,
        string table,
        bool targetProvisioned,
        IReadOnlyList<string> keyColumns,
        JsonSnapshotQualityPolicy quality,
        DataConnectorSchemaBehavior schemaBehavior,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        var validatedSchema = SqlIdentifier.Quote(schema);
        var validatedTable = SqlIdentifier.Quote(table);
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.connector.publish");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        activity?.SetTag("lakehold.connector.name", connectorName);
        activity?.SetTag("lakehold.connector.mode", "incremental");
        var startedAt = TimeProvider.System.GetTimestamp();
        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false);
        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = $"-- Managed connector keyed upsert: {connectorName}\n"
                  + $"DELETE FROM {SqlIdentifier.QuoteName(validatedSchema)}."
                  + $"{SqlIdentifier.QuoteName(validatedTable)} USING '<connector delta>' "
                  + "WHERE <declared key match>;\n"
                  + $"INSERT INTO {SqlIdentifier.QuoteName(validatedSchema)}."
                  + $"{SqlIdentifier.QuoteName(validatedTable)} BY NAME SELECT * FROM '<connector delta>';",
            StartedUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            var result = await JsonSnapshotImporter.UpsertAsync(
                    duckling,
                    filePath,
                    validatedSchema,
                    validatedTable,
                    targetProvisioned,
                    keyColumns,
                    quality,
                    schemaBehavior,
                    $"lakehold.connector:{connectorId}",
                    cancellationToken)
                .ConfigureAwait(false);
            run.Succeeded = true;
            run.RowCount = (int)Math.Min(result.RowsPublished, int.MaxValue);
            run.ElapsedMilliseconds = result.Elapsed.TotalMilliseconds;
            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeSuccess);
            activity?.SetTag(LakeholdTelemetry.RowsKey, result.RowsPublished);
            LakeholdTelemetry.QueryRows.Record(result.RowsPublished);
            return result;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            await SaveQueryRunAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Validates a reusable definition with DuckDB's parser without executing submitted SQL or
    ///     recording an audit run. The normal catalog session is used because a newly provisioned
    ///     local catalog does not have a metadata file that can be its first read-only attachment.
    /// </summary>
    public async Task<bool> IsReadQueryAsync(
        string tenantSlug,
        string catalogName,
        string sql,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false);
        return await duckling.IsReadQueryAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Executes <paramref name="sql"/> against a tenant's catalog and streams the result to the
    ///     caller row by row, recording the run exactly as <see cref="ExecuteAsync"/> does.
    /// </summary>
    /// <remarks>
    ///     The wire-protocol endpoint enters the engine here rather than beside it, so a BI client's
    ///     statements resolve their catalog through the same tenant check, queue on the same session
    ///     gate, and land in the same query history as anything submitted over HTTP. A second entry
    ///     point into <see cref="DucklingPool"/> would have had to re-derive all three.
    /// </remarks>
    public async Task<long> StreamAsync(
        string tenantSlug,
        string catalogName,
        string sql,
        Func<IReadOnlyList<StreamColumn>, CancellationToken, Task> onColumns,
        Duckling.RowHandler onRow,
        int maxRows,
        CancellationToken cancellationToken,
        bool readOnly = false,
        int? tokenId = null)
    {
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.query");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken, readOnly).ConfigureAwait(false);

        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = sql,
            StartedUtc = DateTimeOffset.UtcNow,
            TokenId = tokenId,
        };

        try
        {
            var rows = await duckling
                .StreamQueryAsync(sql, onColumns, onRow, maxRows, cancellationToken)
                .ConfigureAwait(false);

            run.Succeeded = true;
            run.RowCount = (int)Math.Min(rows, int.MaxValue);
            run.ElapsedMilliseconds = TimeProvider.System.GetElapsedTime(startedAt).TotalMilliseconds;

            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeSuccess);
            activity?.SetTag(LakeholdTelemetry.RowsKey, rows);
            LakeholdTelemetry.QueryRows.Record(rows);

            return rows;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;

            RecordQuery(activity, startedAt, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            await SaveQueryRunAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Persists successful and failed operations without allowing audit storage to mask their
    ///     actual result.
    /// </summary>
    private async Task SaveQueryRunAsync(QueryRun run)
    {
        try
        {
            run.ElapsedMilliseconds = run.ElapsedMilliseconds is 0
                ? (DateTimeOffset.UtcNow - run.StartedUtc).TotalMilliseconds
                : run.ElapsedMilliseconds;

            _context.QueryRuns.Add(run);
            await _context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Losing an audit row is preferable to losing the operation result or its real error.
        }
    }

    /// <summary>Records a statement's duration against its outcome, and stamps the span to match.</summary>
    private static void RecordQuery(Activity? activity, long startedAt, string outcome)
    {
        LakeholdTelemetry.QueryDuration.Record(
            TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
            new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, outcome));

        activity?.SetTag(LakeholdTelemetry.OutcomeKey, outcome);
    }

    /// <summary>Returns the schema tree of a tenant's catalog.</summary>
    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await CatalogBrowser.ReadSchemasAsync(duckling, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the storage footprint of a tenant's catalog, table by table.</summary>
    /// <remarks>
    ///     A read, so it declares no more than <c>TenantData</c> and needs no writable attachment: a
    ///     reader who cannot run compaction can still see that compaction is needed.
    /// </remarks>
    public async Task<CatalogStorageInfo> GetStorageAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await StorageBrowser.ReadAsync(duckling, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists one table's data files, optionally as they stood at a given snapshot.</summary>
    public async Task<TableFileList> GetTableFilesAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long? snapshotId,
        int maxRows,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await StorageBrowser
            .ListFilesAsync(duckling, schema, table, snapshotId, maxRows, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns one table's logical, storage, and partition detail.</summary>
    public async Task<TableDetailInfo> GetTableDetailAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await TableInspector
            .ReadAsync(duckling, schema, table, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Profiles every column over the live logical rows of one table or view.</summary>
    public async Task<TableProfileInfo> GetTableProfileAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long? snapshotId,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await ColumnProfiler
            .ReadAsync(duckling, schema, table, snapshotId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns a bounded distribution for one table column.</summary>
    public async Task<ColumnDistributionInfo> GetColumnDistributionAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        string column,
        long? snapshotId,
        int maxBuckets,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await ColumnProfiler
            .ReadDistributionAsync(
                duckling, schema, table, column, snapshotId, maxBuckets, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns a tenant catalog's snapshot history, newest first.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(
        string tenantSlug,
        string catalogName,
        int limit,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await LakehouseMaintenance.ListSnapshotsAsync(duckling, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns one stable keyset window of retained snapshots.</summary>
    public async Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(
        string tenantSlug,
        string catalogName,
        int limit,
        long? upperSnapshotInclusive,
        long? beforeSnapshotExclusive,
        DateTimeOffset? committedFromInclusive,
        DateTimeOffset? committedToInclusive,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await LakehouseMaintenance
            .ListSnapshotsAsync(
                duckling,
                limit,
                upperSnapshotInclusive,
                beforeSnapshotExclusive,
                committedFromInclusive,
                committedToInclusive,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads a bounded table preview at an exact retained snapshot. The relation is constructed
    ///     from quoted identifiers and a numeric version; caller SQL never enters this path.
    /// </summary>
    public async Task<QueryResult> ReadTableAtSnapshotAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long snapshotId,
        int limit,
        CancellationToken cancellationToken,
        int? tokenId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var relation = $"{SqlIdentifier.QuoteName(schema)}.{SqlIdentifier.QuoteName(table)}";
        var readLimit = checked(limit + 1);
        var sql = $"SELECT * FROM {relation} AT (VERSION => {snapshotId.ToString(CultureInfo.InvariantCulture)}) "
                  + $"LIMIT {readLimit.ToString(CultureInfo.InvariantCulture)}";
        var result = await ExecuteAsync(
                tenantSlug,
                catalogName,
                sql,
                cancellationToken,
                readOnly: true,
                tokenId)
            .ConfigureAwait(false);
        return new QueryResult
        {
            Columns = result.Columns,
            Rows = result.Rows.Take(limit).ToArray(),
            Truncated = result.Truncated || result.Rows.Count > limit,
            RowsAffected = result.RowsAffected,
            Elapsed = result.Elapsed,
        };
    }

    /// <summary>
    ///     Plans or atomically applies a table-data restore while preserving the current table
    ///     definition and enforcing its constraints.
    /// </summary>
    public async Task<TableRestoreResult> RestoreTableAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long snapshotId,
        bool apply,
        long? expectedCurrentSnapshotId,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await TableRestore
            .RunAsync(
                duckling,
                schema,
                table,
                snapshotId,
                apply,
                expectedCurrentSnapshotId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Runs a named maintenance operation against a tenant's catalog.</summary>
    /// <param name="apply">
    ///     Whether a destructive operation actually commits. Defaults to false, so <c>expire</c> and
    ///     <c>cleanup</c> report what they would remove and change nothing until a caller explicitly
    ///     confirms. Expiry destroys time-travel history and cleanup deletes data files; neither is
    ///     recoverable, so the safe path is the default one.
    /// </param>
    public async Task<MaintenanceResult> RunMaintenanceAsync(
        string tenantSlug,
        string catalogName,
        string operation,
        bool apply,
        CancellationToken cancellationToken)
    {
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.maintenance");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        activity?.SetTag(LakeholdTelemetry.OperationKey, operation);
        activity?.SetTag(LakeholdTelemetry.DryRunKey, !apply);
        var startedAt = TimeProvider.System.GetTimestamp();

        try
        {
            var result = await RunMaintenanceCoreAsync(tenantSlug, catalogName, operation, apply, cancellationToken)
                .ConfigureAwait(false);

            RecordMaintenance(startedAt, operation, apply, LakeholdTelemetry.OutcomeSuccess);
            return result;
        }
        catch (Exception ex)
        {
            RecordMaintenance(startedAt, operation, apply, LakeholdTelemetry.OutcomeError);
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    private static void RecordMaintenance(long startedAt, string operation, bool apply, string outcome)
        => LakeholdTelemetry.MaintenanceDuration.Record(
            TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds,
            new KeyValuePair<string, object?>(LakeholdTelemetry.OperationKey, operation),
            new KeyValuePair<string, object?>(LakeholdTelemetry.DryRunKey, !apply),
            new KeyValuePair<string, object?>(LakeholdTelemetry.OutcomeKey, outcome));

    private async Task<MaintenanceResult> RunMaintenanceCoreAsync(
        string tenantSlug,
        string catalogName,
        string operation,
        bool apply,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        // Retention is deployment policy rather than a caller parameter: a request must not be able
        // to pass "older_than => now" and expire the history it is standing on.
        var retentionCutoff = DateTimeOffset.UtcNow - _options.SnapshotRetention;
        if (operation == "expire")
        {
            var blocker = await FindCdcRetentionBlockerAsync(
                    tenantSlug,
                    catalogName,
                    duckling,
                    retentionCutoff,
                    cancellationToken)
                .ConfigureAwait(false);
            if (blocker is not null)
            {
                if (apply)
                {
                    throw new InvalidOperationException(blocker);
                }

                return new MaintenanceResult(
                    "expire",
                    $"CDC retention watermark blocks apply: {blocker}",
                    TimeSpan.Zero,
                    DryRun: true);
            }
        }

        return operation switch
        {
            // Non-destructive: they rewrite storage layout without dropping recoverable state.
            "flush" => await LakehouseMaintenance.FlushInlinedDataAsync(duckling, cancellationToken).ConfigureAwait(false),
            "compact" => await LakehouseMaintenance.CompactAsync(duckling, cancellationToken).ConfigureAwait(false),
            "backup" => await LakehouseMaintenance.BackupCatalogAsync(duckling, _options, cancellationToken).ConfigureAwait(false),

            "expire" => await LakehouseMaintenance
                .ExpireSnapshotsAsync(duckling, retentionCutoff, dryRun: !apply, cancellationToken).ConfigureAwait(false),
            "cleanup" => await LakehouseMaintenance
                .CleanupOldFilesAsync(duckling, retentionCutoff, dryRun: !apply, cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentException(
                $"Unknown maintenance operation '{operation}'. Expected flush, compact, backup, expire, or cleanup.",
                nameof(operation)),
        };
    }

    private async Task<string?> FindCdcRetentionBlockerAsync(
        string tenantSlug,
        string catalogName,
        Duckling duckling,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        var subscriptionCursors = await _context.ChangeSubscriptions
            .AsNoTracking()
            // Pause stops delivery, not ownership of the pending history. Deleting the subscription
            // is the explicit operation that releases this retention watermark.
            .Where(s => s.Tenant.Slug == tenantSlug && s.CatalogName == catalogName)
            .Select(s => new { Kind = "subscription", Name = s.Id.ToString(), Cursor = s.LastDeliveredSnapshot })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var consumerCursors = await _context.CdcConsumers
            .AsNoTracking()
            .Where(c => c.Active && c.Tenant.Slug == tenantSlug && c.CatalogName == catalogName)
            .Select(c => new { Kind = "consumer", c.Name, Cursor = c.LastAppliedSnapshot })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var latest = await ChangeFeed.LatestSnapshotAsync(duckling, cancellationToken).ConfigureAwait(false);
        if (latest is null)
        {
            return null;
        }

        foreach (var cursor in subscriptionCursors
                     .Concat(consumerCursors)
                     .Where(item => item.Cursor < latest.Value)
                     .OrderBy(item => item.Cursor))
        {
            var required = cursor.Cursor + 1;
            var snapshot = await LakehouseMaintenance
                .GetSnapshotAsync(duckling, required, cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return $"{cursor.Kind} '{cursor.Name}' requires snapshot {required}, which is no longer retained.";
            }

            if (snapshot.CommittedAt < olderThan)
            {
                return $"{cursor.Kind} '{cursor.Name}' requires snapshot {required} committed "
                       + $"{snapshot.CommittedAt:O}. Advance, abandon, or re-bootstrap it before expiry.";
            }
        }

        return null;
    }

    /// <summary>
    ///     Runs a maintenance operation only if this node can claim the cluster-wide lease for it.
    /// </summary>
    /// <returns>
    ///     The result, or null when another node holds the lease and this node should stand down.
    /// </returns>
    /// <remarks>
    ///     Only the scheduler goes through here. An operator triggering maintenance by hand has
    ///     decided they want it to run on this node, and making that silently do nothing because a
    ///     scheduled sweep holds the lease would be worse than the duplicate work.
    /// </remarks>
    public async Task<MaintenanceResult?> RunScheduledMaintenanceAsync(
        string tenantSlug,
        string catalogName,
        string operation,
        string nodeId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        var acquired = await MaintenanceLease
            .TryAcquireAsync(duckling, operation, nodeId, leaseDuration, cancellationToken)
            .ConfigureAwait(false);

        LakeholdTelemetry.MaintenanceLeaseAttempts.Add(
            1,
            new KeyValuePair<string, object?>(LakeholdTelemetry.OperationKey, operation),
            new KeyValuePair<string, object?>(
                LakeholdTelemetry.ResultKey, acquired ? "acquired" : "held_elsewhere"));

        if (!acquired)
        {
            return null;
        }

        try
        {
            return await RunMaintenanceAsync(tenantSlug, catalogName, operation, apply: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await MaintenanceLease
                .ReleaseAsync(duckling, operation, nodeId, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Lists backup generations for a tenant's catalog, newest first.</summary>
    public async Task<IReadOnlyList<BackupGeneration>> ListBackupsAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        // Resolving through the tenant keeps backup listing inside the same isolation boundary as
        // querying: you cannot enumerate another tenant's generations by guessing a catalog name.
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await CatalogRestore
            .ListGenerationsAsync(
                _options,
                resolved.Descriptor.TenantKey,
                catalogName,
                configure: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Rebuilds a catalog from a backup into a new metadata file.</summary>
    /// <param name="targetMetadataPath">
    ///     Where to write the rebuilt catalog. A bare name or relative path is taken as relative to
    ///     <see cref="LakehouseOptions.MetadataRoot"/>, so it lands beside the catalogs it belongs
    ///     with; an absolute path is used as given.
    /// </param>
    /// <remarks>
    ///     The anchor is deliberate. Left to the framework a relative target resolves against the
    ///     server's working directory, which is wherever the API process happened to be started —
    ///     next to the binary in development, and somewhere no operator would look under Docker. A
    ///     restore that succeeds and leaves the catalog somewhere unexpected is a bad outcome for an
    ///     operation whose entire purpose is recovery. <c>MetadataRoot</c> is where provisioning puts
    ///     every catalog's metadata file, so it is the directory a bare name already means.
    /// </remarks>
    public async Task<CatalogRestoreResult> RestoreBackupAsync(
        string tenantSlug,
        string catalogName,
        string? generation,
        string targetMetadataPath,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        var target = string.IsNullOrWhiteSpace(targetMetadataPath) || Path.IsPathRooted(targetMetadataPath)
            ? targetMetadataPath
            : Path.Combine(_options.MetadataRoot, targetMetadataPath);

        return await CatalogRestore
            .RestoreAsync(
                _options,
                catalogName,
                generation,
                target,
                resolved.Descriptor.DataPath,
                cancellationToken,
                tenantKey: resolved.Descriptor.TenantKey)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Writes a verified, reader-agnostic eject bundle of a tenant's catalog.
    /// </summary>
    /// <param name="includeHistory">
    ///     Whether to also copy the metadata catalog so snapshots and time travel survive the export.
    /// </param>
    /// <remarks>
    ///     Runs under the session gate like backup, so no write can land mid-export and the
    ///     attestation describes one consistent snapshot of the catalog.
    /// </remarks>
    public async Task<CatalogEjectResult> EjectAsync(
        string tenantSlug,
        string catalogName,
        bool includeHistory,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await CatalogEject
            .RunAsync(duckling, _options, includeHistory, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists eject bundles for a tenant's catalog, newest first.</summary>
    public async Task<IReadOnlyList<EjectBundle>> ListEjectsAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        // Same isolation rule as backups: resolve through the tenant first, so bundles cannot be
        // enumerated by guessing another tenant's catalog name.
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return CatalogEject.ListBundles(_options, resolved.Descriptor.TenantKey, catalogName);
    }

    /// <summary>Returns the newest snapshot id of a tenant's catalog, or null when it has none.</summary>
    public async Task<long?> GetLatestSnapshotAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await ChangeFeed.LatestSnapshotAsync(duckling, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns one retained snapshot, or null when its history is unavailable.</summary>
    public async Task<SnapshotInfo?> GetSnapshotAsync(
        string tenantSlug,
        string catalogName,
        long snapshotId,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await LakehouseMaintenance
            .GetSnapshotAsync(duckling, snapshotId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists the base tables a change subscription can watch in a tenant's catalog.</summary>
    public async Task<IReadOnlyList<(string Schema, string Table)>> ListChangeTablesAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await ChangeFeed.ListTablesAsync(duckling, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads a table's row-level changes over an inclusive snapshot range — the pull half of the
    ///     change-data-capture surface, and the fidelity backstop when a webhook payload truncates.
    /// </summary>
    public async Task<ChangeFeedPage> GetChangesAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long fromSnapshot,
        long toSnapshot,
        int maxRows,
        CancellationToken cancellationToken)
        => await GetChangesAsync(
                tenantSlug,
                catalogName,
                schema,
                table,
                fromSnapshot,
                toSnapshot,
                maxRows,
                cursor: null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    ///     Reads one resumable page of a table's row-level changes over an inclusive snapshot range.
    /// </summary>
    public async Task<ChangeFeedPage> GetChangesAsync(
        string tenantSlug,
        string catalogName,
        string schema,
        string table,
        long fromSnapshot,
        long toSnapshot,
        int maxRows,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await ChangeFeed
            .ReadAsync(duckling, schema, table, fromSnapshot, toSnapshot, maxRows, cursor, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Drops the node-local warm session derived from a catalog's stored configuration.
    /// </summary>
    /// <remarks>
    ///     Catalog descriptors are always re-read from PostgreSQL. Only the already-attached
    ///     in-process session needs explicit eviction; its key also includes the persisted
    ///     configuration version, so another node's update naturally selects a new session.
    /// </remarks>
    /// <param name="catalog">The tenant-qualified catalog whose session should be discarded.</param>
    public async Task ForgetCatalogAsync(CatalogDescriptor catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await _pool.EvictAsync(catalog.TenantKey, catalog.CatalogId).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves a tenant's catalog from PostgreSQL.
    /// </summary>
    /// <remarks>
    ///     This intentionally re-reads the shared control plane for each operation. A process-local
    ///     cache cannot observe a catalog update committed through another API node; correctness is
    ///     more important than avoiding this indexed lookup. The descriptor's configuration version
    ///     then prevents an already-warm session from masking the newly observed settings.
    /// </remarks>
    private async Task<ResolvedCatalog> ResolveCatalogAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var catalog = await _context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

        return new ResolvedCatalog(catalog.ToDescriptor(), catalog.TenantId);
    }

    private async Task<(Duckling Duckling, int TenantId)> ResolveAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken,
        bool readOnly = false)
    {
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        // A read-only credential produces a read-only attachment even when the catalog is writable:
        // capability is enforced by the engine not holding a writable handle, not by inspecting SQL.
        // A catalog already configured read-only stays as it is — there is nothing to narrow.
        var descriptor = readOnly && !resolved.Descriptor.ReadOnly
            ? resolved.Descriptor with { ReadOnly = true }
            : resolved.Descriptor;

        var duckling = await _pool
            .GetOrStartAsync(descriptor, configure: null, cancellationToken)
            .ConfigureAwait(false);

        return (duckling, resolved.TenantId);
    }
}

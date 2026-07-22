using System.Diagnostics;
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
    CatalogCache catalogs,
    IOptions<LakehouseOptions> options)
{
    private readonly ControlPlaneContext _context = context;
    private readonly DucklingPool _pool = pool;
    private readonly CatalogCache _catalogs = catalogs;
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
        CancellationToken cancellationToken)
    {
        // The span carries tenant and catalog; the metrics deliberately do not. Per-tenant time
        // series would blow a metrics backend's cardinality budget on a multi-tenant node, and a slow
        // tenant is still findable through the trace.
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.query");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = sql,
            StartedUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            var result = await duckling.ExecuteQueryAsync(sql, cancellationToken).ConfigureAwait(false);

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
            // History is written on both paths — a failed query is exactly what a user comes to
            // the history panel to find. Recording must not mask the original failure, so a
            // history write that fails is swallowed rather than replacing the real exception.
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
                // Losing an audit row is preferable to losing the query result or the real error.
            }
        }
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
        CancellationToken cancellationToken)
    {
        using var activity = LakeholdTelemetry.Source.StartActivity("lakehold.query");
        activity?.SetTag(LakeholdTelemetry.TenantKey, tenantSlug);
        activity?.SetTag(LakeholdTelemetry.CatalogKey, catalogName);
        var startedAt = TimeProvider.System.GetTimestamp();

        var (duckling, tenantId) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        var run = new QueryRun
        {
            TenantId = tenantId,
            CatalogName = catalogName,
            Sql = sql,
            StartedUtc = DateTimeOffset.UtcNow,
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
                // Same trade as the materialising path: losing an audit row beats losing the error.
            }
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

        // Retention windows are fixed rather than caller-supplied: a caller should not be able to
        // pass "older_than => now" and expire the history they are standing on.
        var weekAgo = DateTimeOffset.UtcNow.AddDays(-7);

        return operation switch
        {
            // Non-destructive: they rewrite storage layout without dropping recoverable state.
            "flush" => await LakehouseMaintenance.FlushInlinedDataAsync(duckling, cancellationToken).ConfigureAwait(false),
            "compact" => await LakehouseMaintenance.CompactAsync(duckling, cancellationToken).ConfigureAwait(false),
            "backup" => await LakehouseMaintenance.BackupCatalogAsync(duckling, _options, cancellationToken).ConfigureAwait(false),

            "expire" => await LakehouseMaintenance
                .ExpireSnapshotsAsync(duckling, weekAgo, dryRun: !apply, cancellationToken).ConfigureAwait(false),
            "cleanup" => await LakehouseMaintenance
                .CleanupOldFilesAsync(duckling, weekAgo, dryRun: !apply, cancellationToken).ConfigureAwait(false),

            _ => throw new ArgumentException(
                $"Unknown maintenance operation '{operation}'. Expected flush, compact, backup, expire, or cleanup.",
                nameof(operation)),
        };
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
        _ = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return await CatalogRestore
            .ListGenerationsAsync(_options, catalogName, configure: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Rebuilds a catalog from a backup into a new metadata file.</summary>
    public async Task<CatalogRestoreResult> RestoreBackupAsync(
        string tenantSlug,
        string catalogName,
        string? generation,
        string targetMetadataPath,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await CatalogRestore
            .RestoreAsync(_options, catalogName, generation, targetMetadataPath, resolved.Descriptor.DataPath, cancellationToken)
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
        _ = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return CatalogEject.ListBundles(_options, catalogName);
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
    {
        var (duckling, _) = await ResolveAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await ChangeFeed
            .ReadAsync(duckling, schema, table, fromSnapshot, toSnapshot, maxRows, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Drops every piece of cached state derived from a catalog's stored configuration, so the
    ///     next query re-reads the record and reattaches the session. Call this from any path that
    ///     changes a catalog's configuration.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two layers cache a catalog and both are keyed by catalog name: the resolved descriptor
    ///         in <see cref="CatalogCache"/>, and the warm session in <see cref="DucklingPool"/> whose
    ///         DuckDB instance already has the catalog attached. Dropping only one is not enough —
    ///         keeping the descriptor replays the old configuration into a fresh session, and keeping
    ///         the session runs the old attachment under a fresh descriptor. Neither failure raises
    ///         anything: the configuration change simply never takes effect, which is why the two
    ///         invalidations live behind one call rather than being left to each caller to pair up.
    ///     </para>
    ///     <para>
    ///         Order matters. The descriptor is dropped first because
    ///         <see cref="DucklingPool.GetOrStartAsync"/> keys sessions by catalog name and returns an
    ///         existing one regardless of the descriptor passed in: evicting first would let a
    ///         concurrent query resolve from the still-stale cache and start a replacement session on
    ///         the old configuration, which then outlives this call. Dropping the descriptor first
    ///         leaves only a transient window in which an in-flight query finishes against the old
    ///         session, after which everything is consistent.
    ///     </para>
    /// </remarks>
    /// <param name="catalogName">The catalog whose cached state should be discarded.</param>
    public async Task ForgetCatalogAsync(string catalogName)
    {
        _catalogs.Invalidate(catalogName);
        await _pool.EvictAsync(catalogName).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves a tenant's catalog, reading the control plane only on a cache miss.
    /// </summary>
    /// <remarks>
    ///     Tenant isolation is enforced here: an entry is cached only after a query that matches both
    ///     the tenant slug and the catalog name succeeds, so a cache hit is proof the caller owns the
    ///     catalog exactly as a fresh read would be.
    /// </remarks>
    private async Task<ResolvedCatalog> ResolveCatalogAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        if (_catalogs.TryGet(tenantSlug, catalogName, out var cached))
        {
            return cached;
        }

        var catalog = await _context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

        var resolved = new ResolvedCatalog(catalog.ToDescriptor(), catalog.TenantId);
        _catalogs.Set(tenantSlug, catalogName, resolved);
        return resolved;
    }

    private async Task<(Duckling Duckling, int TenantId)> ResolveAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        var duckling = await _pool
            .GetOrStartAsync(resolved.Descriptor, configure: null, cancellationToken)
            .ConfigureAwait(false);

        return (duckling, resolved.TenantId);
    }
}

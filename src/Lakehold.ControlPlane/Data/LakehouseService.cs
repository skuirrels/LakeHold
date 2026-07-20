using Microsoft.EntityFrameworkCore;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
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
        CancellationToken cancellationToken)
    {
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

            return result;
        }
        catch (Exception ex)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
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
        var catalog = await ResolveCatalogAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);

        return await CatalogRestore
            .RestoreAsync(_options, catalogName, generation, targetMetadataPath, catalog.DataPath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LakeCatalog> ResolveCatalogAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
        => await _context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

    private async Task<(Duckling Duckling, int TenantId)> ResolveAsync(
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
    {
        var catalog = await _context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(
                c => c.Tenant.Slug == tenantSlug && c.Name == catalogName,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new CatalogNotFoundException(
                $"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");

        var duckling = await _pool
            .GetOrStartAsync(catalog.ToDescriptor(), configure: null, cancellationToken)
            .ConfigureAwait(false);

        return (duckling, catalog.TenantId);
    }
}

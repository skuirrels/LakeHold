using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Data;

namespace Lakehold.Api.Endpoints;

/// <summary>Query, catalog, and maintenance endpoints, all scoped to a tenant.</summary>
public static class LakehouseEndpoints
{
    /// <summary>Maps the tenant-scoped lakehouse API.</summary>
    public static IEndpointRouteBuilder MapLakehouseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var tenants = app.MapGroup("/api/tenants").WithTags("Lakehouse");

        tenants.MapGet("/", ListTenantsAsync)
            .WithSummary("Lists tenants and their catalogs.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query", ExecuteAsync)
            .WithSummary("Executes a statement against a tenant's catalog.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/schemas", GetSchemasAsync)
            .WithSummary("Returns the catalog's schema tree.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/snapshots", GetSnapshotsAsync)
            .WithSummary("Returns the catalog's snapshot history for time travel.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/maintenance/{operation}", RunMaintenanceAsync)
            .WithSummary("Runs a maintenance operation: flush, compact, backup, expire, or cleanup.");

        tenants.MapGet("/{tenantSlug}/history", GetHistoryAsync)
            .WithSummary("Returns recent query runs for a tenant.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/backups", ListBackupsAsync)
            .WithSummary("Lists catalog metadata backup generations, newest first.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/backups/restore", RestoreBackupAsync)
            .WithSummary("Rebuilds a catalog from a backup into a new metadata file.");

        app.MapGet("/api/maintenance/schedule", GetScheduledRuns)
            .WithTags("Lakehouse")
            .WithSummary("Recent scheduled maintenance runs.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<TenantDto>>> ListTenantsAsync(
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var tenants = await context.Tenants
            .AsNoTracking()
            .Include(t => t.Catalogs)
            .OrderBy(t => t.DisplayName)
            .Select(t => new TenantDto(
                t.Slug,
                t.DisplayName,
                t.Catalogs
                    .OrderBy(c => c.Name)
                    .Select(c => new CatalogDto(c.Name, c.DataPath, c.IsReadOnly))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<TenantDto>>(tenants);
    }

    private static async Task<Results<Ok<QueryResponse>, NotFound<string>, BadRequest<string>>> ExecuteAsync(
        string tenantSlug,
        string catalogName,
        ExecuteRequest request,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Sql))
        {
            return TypedResults.BadRequest("A SQL statement is required.");
        }

        try
        {
            var result = await lakehouse
                .ExecuteAsync(tenantSlug, catalogName, request.Sql, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new QueryResponse(
                [.. result.Columns.Select(c => new ColumnDto(c.Name, c.DataType, c.ClrType))],
                result.Rows,
                result.Truncated,
                result.Elapsed.TotalMilliseconds));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // A syntax or semantic error is the user's, not the server's. Return the engine's
            // message verbatim — it names the offending token, which is the whole point of an IDE.
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<SchemaDto>>, NotFound<string>>> GetSchemasAsync(
        string tenantSlug,
        string catalogName,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        try
        {
            var schemas = await lakehouse
                .GetSchemasAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok<IReadOnlyList<SchemaDto>>(
            [
                .. schemas.Select(s => new SchemaDto(
                    s.Name,
                    [
                        .. s.Tables.Select(t => new SchemaTableDto(
                            t.Name,
                            t.Kind,
                            [.. t.Columns.Select(c => new SchemaColumnDto(c.Name, c.DataType, c.IsNullable))])),
                    ])),
            ]);
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<SnapshotDto>>, NotFound<string>>> GetSnapshotsAsync(
        string tenantSlug,
        string catalogName,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        int limit = 50)
    {
        try
        {
            var snapshots = await lakehouse
                .GetSnapshotsAsync(tenantSlug, catalogName, Math.Clamp(limit, 1, 500), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok<IReadOnlyList<SnapshotDto>>(
                [.. snapshots.Select(s => new SnapshotDto(s.SnapshotId, s.CommittedAt, s.SchemaVersion, s.CommitMessage))]);
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    private static async Task<Results<Ok<MaintenanceDto>, NotFound<string>, BadRequest<string>>> RunMaintenanceAsync(
        string tenantSlug,
        string catalogName,
        string operation,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        bool apply = false)
    {
        try
        {
            var result = await lakehouse
                .RunMaintenanceAsync(tenantSlug, catalogName, operation, apply, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new MaintenanceDto(
                result.Operation,
                result.Detail,
                result.Elapsed.TotalMilliseconds,
                result.DryRun));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<BackupGenerationDto>>, NotFound<string>>> ListBackupsAsync(
        string tenantSlug,
        string catalogName,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        try
        {
            var generations = await lakehouse
                .ListBackupsAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok<IReadOnlyList<BackupGenerationDto>>(
            [
                .. generations.Select(g => new BackupGenerationDto(
                    g.Generation,
                    g.Manifest?.CreatedUtc,
                    g.Manifest?.SnapshotId,
                    g.Manifest?.Tables.Count ?? 0,
                    g.IsComplete)),
            ]);
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    private static async Task<Results<Ok<RestoreResponse>, NotFound<string>, BadRequest<string>>> RestoreBackupAsync(
        string tenantSlug,
        string catalogName,
        RestoreRequest request,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetMetadataPath))
        {
            return TypedResults.BadRequest("A target metadata path is required.");
        }

        try
        {
            var result = await lakehouse
                .RestoreBackupAsync(tenantSlug, catalogName, request.Generation, request.TargetMetadataPath, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new RestoreResponse(
                result.MetadataPath, result.Generation, result.TablesRestored, result.RowsRestored));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Refusals are expected outcomes here — an incomplete generation, or a target that
            // already exists — so they are the caller's problem to fix, not a server fault.
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static Ok<IReadOnlyList<ScheduledRunDto>> GetScheduledRuns(ScheduledRunLog log)
        => TypedResults.Ok<IReadOnlyList<ScheduledRunDto>>(
        [
            .. log.Recent().Select(r => new ScheduledRunDto(
                r.Job, r.Tenant, r.Catalog, r.StartedUtc, r.ElapsedMilliseconds, r.Succeeded, r.Detail)),
        ]);

    private static async Task<Ok<IReadOnlyList<QueryRunDto>>> GetHistoryAsync(
        string tenantSlug,
        ControlPlaneContext context,
        CancellationToken cancellationToken,
        int limit = 50)
    {
        var history = await context.QueryRuns
            .AsNoTracking()
            .Where(r => r.Tenant.Slug == tenantSlug)
            .OrderByDescending(r => r.StartedUtc)
            .ThenByDescending(r => r.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(r => new QueryRunDto(
                r.Id,
                r.CatalogName,
                r.Sql,
                r.StartedUtc,
                r.ElapsedMilliseconds,
                r.RowCount,
                r.Succeeded,
                r.Error))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<QueryRunDto>>(history);
    }
}

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Lakehold.Api.Auth;
using Lakehold.Api.Scheduling;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;

namespace Lakehold.Api.Endpoints;

/// <summary>Query, catalog, and maintenance endpoints, all scoped to a tenant.</summary>
public static class LakehouseEndpoints
{
    /// <summary>Maps the tenant-scoped lakehouse API.</summary>
    public static IEndpointRouteBuilder MapLakehouseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Every tenant-scoped path shares one authentication check: the bearer token is resolved to a
        // principal and the route's tenant and catalog are validated against it. See
        // docs/AUTHENTICATION.md; today the filter is permissive for token-less requests.
        var tenants = app.MapGroup("/api/tenants")
            .WithTags("Lakehouse")
            .AddEndpointFilter<LakeholdAuthorizationFilter>();

        tenants.MapGet("/", ListTenantsAsync)
            .RequireCapability(RouteCapability.Listing)
            .WithSummary("Lists tenants and their catalogs, scoped to what the credential may see.");

        // Provisioning and token management share this group's authentication filter.
        tenants.MapAdminEndpoints();

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query", ExecuteAsync)
            .WithSummary("Executes a statement against a tenant's catalog.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/schemas", GetSchemasAsync)
            .WithSummary("Returns the catalog's schema tree.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/snapshots", GetSnapshotsAsync)
            .WithSummary("Returns the catalog's snapshot history for time travel.");

        // Maintenance, restore, and eject change or export the whole catalog: owner operations, not
        // something a reader or editor credential authorises. See docs/AUTHENTICATION.md phase 4.
        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/maintenance/{operation}", RunMaintenanceAsync)
            .RequireCapability(RouteCapability.TenantOwner)
            .WithSummary("Runs a maintenance operation: flush, compact, backup, expire, or cleanup.");

        tenants.MapGet("/{tenantSlug}/history", GetHistoryAsync)
            .WithSummary("Returns recent query runs for a tenant.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/backups", ListBackupsAsync)
            .WithSummary("Lists catalog metadata backup generations, newest first.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/backups/restore", RestoreBackupAsync)
            .RequireCapability(RouteCapability.TenantOwner)
            .WithSummary("Rebuilds a catalog from a backup into a new metadata file.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/eject", EjectAsync)
            .RequireCapability(RouteCapability.TenantOwner)
            .WithSummary("Writes a verified, reader-agnostic eject bundle of the catalog.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/ejects", ListEjectsAsync)
            .WithSummary("Lists eject bundles, newest first.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/changes", GetChangesAsync)
            .WithSummary("Reads a table's row-level changes over an inclusive snapshot range.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/subscriptions", ListSubscriptionsAsync)
            .WithSummary("Lists the catalog's change subscriptions.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/subscriptions", CreateSubscriptionAsync)
            .WithSummary("Creates a webhook subscription to the catalog's change feed.");

        tenants.MapDelete("/{tenantSlug}/catalogs/{catalogName}/subscriptions/{id:int}", DeleteSubscriptionAsync)
            .WithSummary("Deletes a change subscription.");

        app.MapGet("/api/maintenance/schedule", GetScheduledRuns)
            .WithTags("Lakehouse")
            .WithSummary("Recent scheduled maintenance runs.");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<TenantDto>>> ListTenantsAsync(
        HttpContext http,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        // An instance token (and the transitional token-less caller) sees every tenant; a tenant token
        // sees only its own. The scope is applied here rather than in the filter because the filter
        // decides reachability, not projection.
        var principal = http.GetLakeholdPrincipal();
        var ownTenant = principal.IsAuthenticated && principal.Scope == TokenScope.Tenant
            ? principal.TenantSlug
            : null;

        var query = context.Tenants.AsNoTracking().Include(t => t.Catalogs).AsQueryable();
        if (ownTenant is not null)
        {
            query = query.Where(t => t.Slug == ownTenant);
        }

        var tenants = await query
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
        HttpContext http,
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
            // A read-only credential attaches the catalog read-only, so a write fails in the engine.
            // The token id is recorded on the run for the audit trail.
            var principal = http.GetLakeholdPrincipal();
            var result = await lakehouse
                .ExecuteAsync(tenantSlug, catalogName, request.Sql, cancellationToken, principal.IsReadOnly, principal.TokenId)
                .ConfigureAwait(false);

            return TypedResults.Ok(new QueryResponse(
                [.. result.Columns.Select(c => new ColumnDto(c.Name, c.DataType, c.ClrType))],
                result.Rows,
                result.Truncated,
                result.Elapsed.TotalMilliseconds,
                result.RowsAffected));
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

    private static async Task<Results<Ok<EjectResponse>, NotFound<string>, BadRequest<string>>> EjectAsync(
        string tenantSlug,
        string catalogName,
        EjectRequest? request,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await lakehouse
                .EjectAsync(tenantSlug, catalogName, request?.IncludeHistory ?? false, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new EjectResponse(
                result.Location,
                result.TableCount,
                result.TotalRows,
                result.Verified,
                result.DigestDeferred,
                result.IsSigned,
                result.IncludesHistory));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            // A storage-level refusal — an unwritable eject root, a missing bucket permission — is
            // the caller's deployment to fix. A verification mismatch, by contrast, is an
            // InvalidOperationException and deliberately NOT caught here: it means the export cannot
            // be trusted, which is a server fault worth a 500 and an operator's attention.
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<EjectBundleDto>>, NotFound<string>>> ListEjectsAsync(
        string tenantSlug,
        string catalogName,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        try
        {
            var bundles = await lakehouse
                .ListEjectsAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok<IReadOnlyList<EjectBundleDto>>(
            [
                .. bundles.Select(b => new EjectBundleDto(
                    b.Bundle,
                    b.Manifest?.CreatedUtc,
                    b.Manifest?.SnapshotId,
                    b.Manifest?.IncludesHistory ?? false,
                    b.Manifest?.Signature is not null,
                    b.IsComplete,
                    [
                        .. (b.Manifest?.DataTables ?? []).Select(t =>
                            new EjectedTableDto(t.Schema, t.Table, t.RowCount, t.Sha256, t.Bytes)),
                    ])),
            ]);
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    private static async Task<Results<Ok<ChangePageDto>, NotFound<string>, BadRequest<string>>> GetChangesAsync(
        string tenantSlug,
        string catalogName,
        string table,
        long fromSnapshot,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        string schema = "main",
        long? toSnapshot = null,
        int limit = 1000)
    {
        try
        {
            // An open-ended read goes to the newest snapshot, so a consumer can poll with only a
            // cursor and no second round trip to discover the range's end.
            var to = toSnapshot
                ?? await lakehouse.GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false)
                ?? 0;

            var page = await lakehouse
                .GetChangesAsync(
                    tenantSlug, catalogName, schema, table, fromSnapshot, to,
                    Math.Clamp(limit, 1, 10_000), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new ChangePageDto(
                page.Schema,
                page.Table,
                page.FromSnapshot,
                page.ToSnapshot,
                page.Truncated,
                [
                    .. page.Changes.Select(c => new ChangeDto(
                        c.SnapshotId, c.RowId, ChangeTypeName(c.Change), c.Row)),
                ]));
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
            // e.g. an unknown table, or a range whose end predates the table's creation. The engine
            // names the problem precisely; forward it.
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<Results<Ok<IReadOnlyList<SubscriptionDto>>, NotFound<string>>> ListSubscriptionsAsync(
        string tenantSlug,
        string catalogName,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        if (!await TenantOwnsCatalogAsync(context, tenantSlug, catalogName, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        var subscriptions = await context.ChangeSubscriptions
            .AsNoTracking()
            .Where(s => s.Tenant.Slug == tenantSlug && s.CatalogName == catalogName)
            .OrderBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<SubscriptionDto>>([.. subscriptions.Select(ToDto)]);
    }

    private static async Task<Results<Created<SubscriptionDto>, NotFound<string>, BadRequest<string>>> CreateSubscriptionAsync(
        string tenantSlug,
        string catalogName,
        CreateSubscriptionRequest request,
        ControlPlaneContext context,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return TypedResults.BadRequest("An absolute http or https endpoint URL is required.");
        }

        // The model's column ceilings are enforced here because DuckDB does not enforce VARCHAR
        // lengths — without this, an oversized value would silently store and only surface later.
        if (request.EndpointUrl.Length > 2048)
        {
            return TypedResults.BadRequest("The endpoint URL must be at most 2048 characters.");
        }

        // A short secret makes the HMAC decorative. 16 characters is the floor, not a recommendation.
        if (string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length is < 16 or > 256)
        {
            return TypedResults.BadRequest("A signing secret of 16 to 256 characters is required.");
        }

        if (!SqlIdentifier.IsValid(request.Schema)
            || (request.Table is not null && !SqlIdentifier.IsValid(request.Table)))
        {
            return TypedResults.BadRequest("Schema and table must be bare SQL identifiers.");
        }

        var catalog = await context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken)
            .ConfigureAwait(false);
        if (catalog is null)
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        // Start the cursor at the catalog's newest snapshot: a new subscription means "tell me what
        // changes from now on", not "replay this catalog's entire history into my endpoint".
        var latest = await lakehouse
            .GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false) ?? 0;

        var subscription = new ChangeSubscription
        {
            TenantId = catalog.TenantId,
            CatalogName = catalogName,
            SchemaName = request.Schema,
            TableName = request.Table,
            EndpointUrl = request.EndpointUrl,
            Secret = request.Secret,
            LastDeliveredSnapshot = latest,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        context.ChangeSubscriptions.Add(subscription);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.Created(
            $"/api/tenants/{tenantSlug}/catalogs/{catalogName}/subscriptions/{subscription.Id}",
            ToDto(subscription));
    }

    private static async Task<Results<NoContent, NotFound<string>>> DeleteSubscriptionAsync(
        string tenantSlug,
        string catalogName,
        int id,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        // Scoped to the tenant and catalog from the route, so a subscription id alone cannot reach
        // across the isolation boundary.
        var subscription = await context.ChangeSubscriptions
            .FirstOrDefaultAsync(
                s => s.Id == id && s.Tenant.Slug == tenantSlug && s.CatalogName == catalogName,
                cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            return TypedResults.NotFound($"Subscription {id} was not found for '{tenantSlug}/{catalogName}'.");
        }

        context.ChangeSubscriptions.Remove(subscription);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return TypedResults.NoContent();
    }

    private static Task<bool> TenantOwnsCatalogAsync(
        ControlPlaneContext context,
        string tenantSlug,
        string catalogName,
        CancellationToken cancellationToken)
        => context.Catalogs
            .AsNoTracking()
            .AnyAsync(c => c.Tenant.Slug == tenantSlug && c.Name == catalogName, cancellationToken);

    /// <summary>Projects a subscription for the API, deliberately omitting the signing secret.</summary>
    private static SubscriptionDto ToDto(ChangeSubscription s) => new(
        s.Id,
        s.CatalogName,
        s.SchemaName,
        s.TableName,
        s.EndpointUrl,
        s.Active,
        s.LastDeliveredSnapshot,
        s.ConsecutiveFailures,
        s.LastAttemptUtc,
        s.LastError,
        s.CreatedUtc);

    private static string ChangeTypeName(ChangeType change) => change switch
    {
        ChangeType.Insert => "insert",
        ChangeType.Delete => "delete",
        ChangeType.UpdatePreimage => "update_preimage",
        ChangeType.UpdatePostimage => "update_postimage",
        _ => "unknown",
    };

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
                r.Error,
                r.TokenId,
                // Left join by hand: the token may have been deleted, so its name is best-effort and
                // the audit row survives without it.
                context.ApiTokens.Where(t => t.Id == r.TokenId).Select(t => t.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<QueryRunDto>>(history);
    }
}

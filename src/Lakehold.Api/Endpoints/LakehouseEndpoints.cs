using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Lakehold.Api.Auth;
using Lakehold.Api.Cdc;
using Lakehold.Api.Importing;
using Lakehold.Api.Scheduling;
using Lakehold.Api.Querying;
using Lakehold.Querying;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Lakehold.Api.PublicApi;
using Microsoft.AspNetCore.DataProtection;

namespace Lakehold.Api.Endpoints;

/// <summary>Query, catalog, and maintenance endpoints, all scoped to a tenant.</summary>
public static class LakehouseEndpoints
{
    /// <summary>Maps the tenant-scoped lakehouse API.</summary>
    public static IEndpointRouteBuilder MapLakehouseEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/access", GetAccess)
            .WithTags("Lakehouse")
            .AddEndpointFilter<LakeholdAuthorizationFilter>()
            .RequireCapability(Capability.Listing)
            .WithSummary("Describes the caller's effective workbench access.");

        app.MapGet("/query-languages", GetQueryLanguagesAsync)
            .WithTags("Lakehouse")
            .AddEndpointFilter<LakeholdAuthorizationFilter>()
            .RequireCapability(Capability.Listing)
            .WithSummary("Lists healthy query languages installed for the Workbench.");

        // Every tenant-scoped path shares one authentication check: the bearer token is resolved to a
        // principal and the route's tenant and catalog are validated against it. See
        // docs/AUTHENTICATION.md. A request with no credential is refused unless the deployment has
        // configured demo access, which admits a reader scoped to one named catalog.
        var tenants = app.MapGroup("/tenants")
            .WithTags("Lakehouse")
            .AddEndpointFilter<LakeholdAuthorizationFilter>();

        tenants.MapGet("/", ListTenantsAsync)
            .RequireCapability(Capability.Listing)
            .WithCursorPagination<TenantDto>()
            .WithSummary("Lists tenants and their catalogs, scoped to what the credential may see.");

        // Provisioning and token management share this group's authentication filter.
        tenants.MapAdminEndpoints();
        tenants.MapMemberEndpoints();

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query", ExecuteAsync)
            .WithSummary("Executes a statement against a tenant's catalog.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/query:stream", StreamQueryAsync)
            .Produces(StatusCodes.Status200OK, contentType: Ndjson.ContentType)
            .WithName("StreamQuery")
            .WithSummary("Streams a read-only SQL result as schema, row, and completion NDJSON records.");

        tenants.MapGet(
                "/{tenantSlug}/catalogs/{catalogName}/query-languages/{language}/starter",
                GetQueryStarterAsync)
            .WithSummary("Returns a catalog-aware starter expression owned by the selected language planner.");

        tenants.MapTabularImportEndpoints(
            app.ServiceProvider.GetRequiredService<IOptions<CsvUploadOptions>>().Value.MaxBytes);

        tenants.MapSavedQueryEndpoints();
        tenants.MapDataConnectorEndpoints();

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/schemas", GetSchemasAsync)
            .WithSummary("Returns the catalog's schema tree.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/snapshots", GetSnapshotsAsync)
            .WithCursorPagination<SnapshotDto>()
            .WithSummary("Returns a stable keyset page of snapshot history for time travel.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/snapshots/{snapshotId:long}", GetSnapshotAsync)
            .Produces<SnapshotDto>()
            .WithName("GetSnapshot")
            .WithSummary("Returns one retained snapshot by its source-native identifier.");

        tenants.MapGet(
                "/{tenantSlug}/catalogs/{catalogName}/snapshots/{snapshotId:long}/table",
                GetSnapshotTableAsync)
            .Produces<QueryResponse>()
            .WithName("GetSnapshotTable")
            .WithSummary("Returns a bounded table preview at an exact retained snapshot.");

        tenants.MapPost(
                "/{tenantSlug}/catalogs/{catalogName}/snapshots/{snapshotId:long}/restore-table",
                RestoreTableAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithIdempotency()
            .WithSummary("Plans or atomically restores one table's rows from a snapshot.");

        // TenantData, not TenantOwner. Maintenance is the owner's to authorise because it destroys or
        // exports; knowing how large a table is, is not. A reader who cannot press Compact should
        // still be able to see that Compact is needed. See docs/UI.md.
        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/storage", GetStorageAsync)
            .WithSummary("Returns the catalog's storage footprint: sizes, file counts, and maintenance advice.");

        // Schema and table are query parameters rather than route segments: a table name may contain
        // a slash or a dot, and encoding those into the path invites every router and proxy between
        // here and the browser to disagree about what the name was.
        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/storage/files", GetTableFilesAsync)
            .WithSummary("Lists one table's Parquet data files and their paired delete files.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/table-detail", GetTableDetailAsync)
            .WithSummary("Returns one table's schema, storage footprint, and partition layout.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/table-profile", GetTableProfileAsync)
            .WithSummary("Profiles the live logical rows of every column in one table.");

        tenants.MapGet(
                "/{tenantSlug}/catalogs/{catalogName}/column-distribution",
                GetColumnDistributionAsync)
            .WithSummary("Returns a bounded distribution for one table column.");

        // Maintenance, restore, and eject change or export the whole catalog: owner operations, not
        // something a reader or editor credential authorises. See docs/AUTHENTICATION.md phase 4.
        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/maintenance/{operation}", RunMaintenanceAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<MaintenanceDto>()
            .Produces<PublicApiOperationDto>(StatusCodes.Status202Accepted)
            .WithIdempotency()
            .WithSummary("Runs a maintenance operation: flush, compact, backup, expire, or cleanup.");

        tenants.MapGet("/{tenantSlug}/history", GetHistoryAsync)
            .WithCursorPagination<QueryRunDto>(maximumLimit: 200)
            .WithSummary("Returns recent query runs for a tenant.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/backups", ListBackupsAsync)
            .WithCursorPagination<BackupGenerationDto>()
            .WithSummary("Lists catalog metadata backup generations, newest first.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/backups/restore", RestoreBackupAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<PublicApiOperationDto>(StatusCodes.Status202Accepted)
            .WithIdempotency()
            .WithSummary("Rebuilds a catalog from a backup into a new metadata file.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/eject", EjectAsync)
            .RequireCapability(Capability.TenantOwner)
            .Produces<PublicApiOperationDto>(StatusCodes.Status202Accepted)
            .WithIdempotency()
            .WithSummary("Writes a verified, reader-agnostic eject bundle of the catalog.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/ejects", ListEjectsAsync)
            .WithCursorPagination<EjectBundleDto>()
            .WithSummary("Lists eject bundles, newest first.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/changes", GetChangesAsync)
            .WithSummary("Reads a table's row-level changes over an inclusive snapshot range.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/changes:stream", StreamChangesAsync)
            .Produces(StatusCodes.Status200OK, contentType: Ndjson.ContentType)
            .WithName("StreamChanges")
            .WithSummary("Streams a snapshot-frozen table change range as NDJSON.");

        tenants.MapGet(
                "/{tenantSlug}/catalogs/{catalogName}/cdc/snapshots/{snapshot:long}/changes",
                GetSnapshotChangesAsync)
            .WithSummary("Reads one resumable page of a table's changes in one snapshot.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/subscriptions", ListSubscriptionsAsync)
            .WithCursorPagination<SubscriptionDto>()
            .WithSummary("Lists the catalog's change subscriptions.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/subscriptions", CreateSubscriptionAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithIdempotency()
            .WithSummary("Creates a webhook subscription to the catalog's change feed.");

        tenants.MapDelete("/{tenantSlug}/catalogs/{catalogName}/subscriptions/{id:int}", DeleteSubscriptionAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Deletes a change subscription.");

        tenants.MapPut(
                "/{tenantSlug}/catalogs/{catalogName}/subscriptions/{id:int}",
                UpdateSubscriptionAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Pauses, resumes, rotates, retries, or replays a change subscription.");

        tenants.MapGet("/{tenantSlug}/catalogs/{catalogName}/cdc/consumers", ListCdcConsumersAsync)
            .WithCursorPagination<CdcConsumerDto>()
            .WithSummary("Lists durable pull-consumer checkpoints.");

        tenants.MapPost("/{tenantSlug}/catalogs/{catalogName}/cdc/consumers", RegisterCdcConsumerAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithIdempotency()
            .WithSummary("Registers or resumes a durable pull consumer.");

        tenants.MapPut(
                "/{tenantSlug}/catalogs/{catalogName}/cdc/consumers/{id:int}/checkpoint",
                AdvanceCdcConsumerAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Advances a durable pull consumer after its target commit.");

        tenants.MapDelete(
                "/{tenantSlug}/catalogs/{catalogName}/cdc/consumers/{id:int}",
                DeleteCdcConsumerAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Abandons a durable pull consumer and releases its retention watermark.");

        // Outside the /api/tenants group, so it carries the filter itself: the run log names every
        // tenant and catalog the scheduler touched, which is not anonymous-readable. Listing rather
        // than Instance because a tenant credential has a legitimate reason to check its own backups
        // ran — the handler narrows the rows to what the principal may see.
        app.MapGet("/maintenance/schedule", GetScheduledRuns)
            .WithTags("Lakehouse")
            .AddEndpointFilter<LakeholdAuthorizationFilter>()
            .RequireCapability(Capability.Listing)
            .WithCursorPagination<ScheduledRunDto>()
            .WithSummary("Recent scheduled maintenance runs, scoped to what the credential may see.");

        return app;
    }

    internal static Ok<AccessDto> GetAccess(HttpContext http)
    {
        var principal = http.GetLakeholdPrincipal();
        var mode = principal.IsDemo ? "demo" : "authenticated";

        // Asked of the same policy the member and token routes enforce, against the principal's own
        // tenant, so the Workbench offers Users exactly when those routes would answer it. A demo
        // reader, a read-only owner, and a catalog-narrowed owner are all refused there, and a client
        // has no business re-deriving that from the role.
        var tenantAdmin = CapabilityPolicy.Evaluate(
            principal,
            Capability.TenantAdmin,
            principal.TenantSlug,
            catalog: null).Outcome == CapabilityOutcome.Allowed;

        return TypedResults.Ok(new AccessDto(
            mode,
            principal.Role.ToString().ToLowerInvariant(),
            principal.IsReadOnly,
            principal.Scope == TokenScope.Instance,
            tenantAdmin));
    }

    private static async Task<Ok<IReadOnlyList<TenantDto>>> ListTenantsAsync(
        HttpContext http,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        // An instance token sees every tenant; a tenant token sees only its own. The scope is applied
        // here rather than in the filter because the filter decides reachability, not projection.
        var principal = http.GetLakeholdPrincipal();
        var ownTenant = principal.Scope == TokenScope.Tenant
            ? principal.TenantSlug
            : null;

        var query = context.Tenants.AsNoTracking().Include(t => t.Catalogs).AsQueryable();
        if (ownTenant is not null)
        {
            query = query.Where(t => t.Slug == ownTenant);
        }

        var orderedTenants = query.OrderBy(t => t.DisplayName).ThenBy(t => t.Id);
        var tenants = await PublicApiPagination.ApplySourceWindow(orderedTenants, http)
            .Select(t => new TenantDto(
                t.Slug,
                t.DisplayName,
                t.Catalogs
                    .OrderBy(c => c.Name)
                    .Select(c => new CatalogDto(
                        c.Name,
                        c.DataPath,
                        c.IsReadOnly,
                        c.MetadataKind.ToString(),
                        c.StorageKind.ToString(),
                        c.StorageProfile))
                    .ToList()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<TenantDto>>(tenants);
    }

    private static async Task<Results<
        Ok<QueryResponse>,
        NotFound<string>,
        BadRequest<string>,
        BadRequest<QueryPlanningFailure>,
        ProblemHttpResult>> ExecuteAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        ExecuteRequest request,
        QueryExecutionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var source = request?.EffectiveSource;
        var language = request?.EffectiveLanguage ?? "sql";
        if (string.IsNullOrWhiteSpace(source))
        {
            return TypedResults.BadRequest("Query source is required.");
        }

        try
        {
            // A read-only credential attaches the catalog read-only, so a write fails in the engine.
            // The token id is recorded on the run for the audit trail.
            var principal = http.GetLakeholdPrincipal();
            var planned = await coordinator
                .ExecuteAsync(
                    tenantSlug,
                    catalogName,
                    language,
                    source,
                    principal.IsReadOnly,
                    QueryAuditContext.From(
                        principal,
                        http.IsLegacyApiRequest() ? QueryOrigin.Workbench : QueryOrigin.Rest),
                    recordHistory: !principal.IsDemo,
                    cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(QueryResponse.From(
                planned.Result,
                language,
                string.Equals(language, "sql", StringComparison.Ordinal) ? null : planned.Plan.Sql,
                planned.Plan.Diagnostics));
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
        catch (QuerySourceInvalidException ex)
        {
            return TypedResults.BadRequest(new QueryPlanningFailure(ex.Diagnostics));
        }
        catch (QueryLanguageUnavailableException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            return TypedResults.Problem(
                $"The query planner is unavailable: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (QueryPlanRejectedException ex)
        {
            return TypedResults.Problem(
                $"The query planner returned an unsafe plan: {ex.Message}",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> StreamQueryAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        ExecuteRequest request,
        LakehouseService lakehouse,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var source = request?.EffectiveSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                "Query source is required.",
                "query_source_required");
        }

        if (!string.Equals(request!.EffectiveLanguage, "sql", StringComparison.Ordinal))
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                "Streaming currently accepts SQL source only; compile another query language before opening the stream.",
                "streaming_language_not_supported");
        }

        try
        {
            if (!await lakehouse.IsReadQueryAsync(tenantSlug, catalogName, source, cancellationToken)
                    .ConfigureAwait(false))
            {
                return PublicApiProblems.Create(
                    http,
                    StatusCodes.Status400BadRequest,
                    "A streaming query must be one read-only SQL statement.",
                    "streaming_query_not_read_only");
            }

            var principal = http.GetLakeholdPrincipal();
            return new QueryNdjsonResult(
                lakehouse,
                tenantSlug,
                catalogName,
                source,
                QueryAuditContext.From(principal, QueryOrigin.Rest),
                loggerFactory.CreateLogger<QueryNdjsonResult>());
        }
        catch (CatalogNotFoundException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<QueryLanguageDescriptor>>> GetQueryLanguagesAsync(
        QueryExecutionCoordinator coordinator,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await coordinator.GetLanguagesAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<Results<
        Ok<QueryLanguageStarter>,
        NotFound<string>,
        BadRequest<QueryPlanningFailure>,
        ProblemHttpResult>> GetQueryStarterAsync(
        string tenantSlug,
        string catalogName,
        string language,
        QueryExecutionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await coordinator
                .CreateStarterAsync(tenantSlug, catalogName, language, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (QuerySourceInvalidException ex)
        {
            return TypedResults.BadRequest(new QueryPlanningFailure(ex.Diagnostics));
        }
        catch (QueryLanguageUnavailableException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (HttpRequestException ex)
        {
            return TypedResults.Problem(
                $"The query planner is unavailable: {ex.Message}",
                statusCode: StatusCodes.Status503ServiceUnavailable);
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

    /// <summary>Returns the catalog's storage footprint with its maintenance advisories.</summary>
    /// <remarks>
    ///     The advisories are computed here rather than in the engine or the browser so that one place
    ///     owns the threshold, and so a second consumer — an agent tool, a CLI — reaches the same
    ///     verdict as the workbench does.
    /// </remarks>
    internal static async Task<Results<Ok<CatalogStorageDto>, NotFound<string>>> GetStorageAsync(
        string tenantSlug,
        string catalogName,
        LakehouseService lakehouse,
        IOptions<LakehouseOptions> options,
        CancellationToken cancellationToken)
    {
        try
        {
            var storage = await lakehouse
                .GetStorageAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false);

            // The catalog's own setting wins when it has one; the deployment's floor stands in when
            // it does not. Reported alongside the verdict so the basis of the advice is visible.
            var advisory = storage.TargetFileSizeBytes ?? options.Value.CompactionAdvisoryBytes;

            return TypedResults.Ok(new CatalogStorageDto(
                [
                    .. storage.Tables.Select(t => ToTableStorageDto(t, advisory)),
                ],
                storage.TargetFileSizeBytes,
                advisory));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    /// <summary>Returns one table's logical, storage, and partition detail.</summary>
    internal static async Task<Results<Ok<TableDetailDto>, NotFound<string>, BadRequest<string>>>
        GetTableDetailAsync(
            string tenantSlug,
            string catalogName,
            string table,
            LakehouseService lakehouse,
            IOptions<LakehouseOptions> options,
            CancellationToken cancellationToken,
            string schema = "main")
    {
        try
        {
            var detail = await lakehouse
                .GetTableDetailAsync(tenantSlug, catalogName, schema, table, cancellationToken)
                .ConfigureAwait(false);
            var advisory = detail.TargetFileSizeBytes ?? options.Value.CompactionAdvisoryBytes;

            return TypedResults.Ok(new TableDetailDto(
                detail.SchemaName,
                detail.TableName,
                detail.Kind,
                [
                    .. detail.Columns.Select(c => new TableDetailColumnDto(
                        c.Name, c.DataType, c.IsNullable)),
                ],
                detail.Storage is null ? null : ToTableStorageDto(detail.Storage, advisory),
                [
                    .. detail.PartitionSpecs.Select(spec => new PartitionSpecDto(
                        spec.PartitionId,
                        spec.BeginSnapshot,
                        spec.EndSnapshot,
                        [
                            .. spec.Keys.Select(key => new PartitionKeyDto(
                                key.Position, key.ColumnName, key.Transform)),
                        ])),
                ],
                detail.TargetFileSizeBytes,
                advisory));
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

    /// <summary>Profiles all columns over the table's live logical rows.</summary>
    internal static async Task<Results<Ok<TableProfileDto>, NotFound<string>, BadRequest<string>>>
        GetTableProfileAsync(
            string tenantSlug,
            string catalogName,
            string table,
            LakehouseService lakehouse,
            CancellationToken cancellationToken,
            string schema = "main",
            long? snapshot = null)
    {
        try
        {
            var profile = await lakehouse
                .GetTableProfileAsync(
                    tenantSlug, catalogName, schema, table, snapshot, cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new TableProfileDto(
                profile.SchemaName,
                profile.TableName,
                profile.SnapshotId,
                profile.RowCount,
                [
                    .. profile.Columns.Select(column => new ColumnProfileDto(
                        column.Name,
                        column.DataType,
                        column.RowCount,
                        column.NullCount,
                        column.Minimum,
                        column.Maximum,
                        column.ApproxDistinct,
                        column.Mean,
                        column.StandardDeviation,
                        column.FirstQuartile,
                        column.Median,
                        column.ThirdQuartile)),
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
            return TypedResults.BadRequest(ex.Message);
        }
    }

    /// <summary>Returns one bounded column distribution.</summary>
    internal static async Task<Results<Ok<ColumnDistributionDto>, NotFound<string>, BadRequest<string>>>
        GetColumnDistributionAsync(
            string tenantSlug,
            string catalogName,
            string table,
            string column,
            LakehouseService lakehouse,
            CancellationToken cancellationToken,
            string schema = "main",
            long? snapshot = null,
            int limit = 20)
    {
        try
        {
            var distribution = await lakehouse
                .GetColumnDistributionAsync(
                    tenantSlug,
                    catalogName,
                    schema,
                    table,
                    column,
                    snapshot,
                    Math.Clamp(limit, 1, 50),
                    cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new ColumnDistributionDto(
                distribution.SchemaName,
                distribution.TableName,
                distribution.ColumnName,
                distribution.DataType,
                distribution.SnapshotId,
                distribution.Kind,
                distribution.NullCount,
                distribution.Truncated,
                [
                    .. distribution.Buckets.Select(bucket => new DistributionBucketDto(
                        bucket.Label, bucket.LowerBound, bucket.UpperBound, bucket.Count)),
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
            return TypedResults.BadRequest(ex.Message);
        }
    }

    /// <summary>Lists one table's data files, optionally as they stood at a given snapshot.</summary>
    internal static async Task<Results<Ok<TableFilesDto>, NotFound<string>, BadRequest<string>>> GetTableFilesAsync(
        string tenantSlug,
        string catalogName,
        string table,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        string schema = "main",
        long? snapshot = null,
        int limit = 1000)
    {
        try
        {
            var files = await lakehouse
                .GetTableFilesAsync(
                    tenantSlug, catalogName, schema, table, snapshot,
                    Math.Clamp(limit, 1, 10_000), cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new TableFilesDto(
                files.SchemaName,
                files.TableName,
                files.SnapshotId,
                files.Truncated,
                [
                    .. files.Files.Select(f => new DataFileDto(
                        f.DataFile, f.DataFileSizeBytes, f.DeleteFile, f.DeleteFileSizeBytes)),
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
            // e.g. an unknown table, or a snapshot predating the table's creation. The engine names
            // the problem precisely; forward it rather than replacing it with a generic failure.
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static async Task<IResult> GetSnapshotsAsync(
        string tenantSlug,
        string catalogName,
        HttpContext http,
        LakehouseService lakehouse,
        IDataProtectionProvider dataProtection,
        CancellationToken cancellationToken,
        int limit = 100,
        string? cursor = null,
        DateTimeOffset? committedFrom = null,
        DateTimeOffset? committedTo = null)
    {
        if (limit is < 1 or > PublicApiPagination.MaximumLimit)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                $"The limit query parameter must be an integer from 1 to {PublicApiPagination.MaximumLimit}.",
                "invalid_page_limit");
        }
        if (committedFrom > committedTo)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                "committedFrom must not be later than committedTo.",
                "invalid_snapshot_window");
        }

        try
        {
            if (http.IsLegacyApiRequest())
            {
                var legacy = await lakehouse
                    .GetSnapshotsAsync(tenantSlug, catalogName, limit, cancellationToken)
                    .ConfigureAwait(false);
                return TypedResults.Ok<IReadOnlyList<SnapshotDto>>(
                    [.. legacy.Select(ToSnapshotDto)]);
            }

            var scope = SnapshotCursor.Scope(tenantSlug, catalogName, committedFrom, committedTo);
            long? upperSnapshotInclusive = null;
            long? beforeSnapshotExclusive = null;
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                if (!SnapshotCursor.TryDecode(dataProtection, cursor, scope, out var position))
                {
                    return PublicApiProblems.Create(
                        http,
                        StatusCodes.Status400BadRequest,
                        "The cursor is invalid, expired, or belongs to a different snapshot request.",
                        "invalid_cursor");
                }
                upperSnapshotInclusive = position.UpperSnapshotInclusive;
                beforeSnapshotExclusive = position.BeforeSnapshotExclusive;
            }

            var snapshots = await lakehouse
                .GetSnapshotsAsync(
                    tenantSlug,
                    catalogName,
                    limit + 1,
                    upperSnapshotInclusive,
                    beforeSnapshotExclusive,
                    committedFrom,
                    committedTo,
                    cancellationToken)
                .ConfigureAwait(false);

            var page = snapshots.Take(limit).Select(ToSnapshotDto).ToArray();
            var frozenUpper = upperSnapshotInclusive ?? (page.FirstOrDefault()?.SnapshotId ?? 0);
            var nextCursor = snapshots.Count > limit && page.Length > 0
                ? SnapshotCursor.Encode(dataProtection, scope, frozenUpper, page[^1].SnapshotId)
                : null;
            return TypedResults.Ok(new CursorPage<SnapshotDto>(page, nextCursor));
        }
        catch (CatalogNotFoundException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status404NotFound, ex.Message);
        }
    }

    private static async Task<IResult> GetSnapshotAsync(
        string tenantSlug,
        string catalogName,
        long snapshotId,
        HttpContext http,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await lakehouse
                .GetSnapshotAsync(tenantSlug, catalogName, snapshotId, cancellationToken)
                .ConfigureAwait(false);
            return snapshot is null
                ? PublicApiProblems.Create(
                    http,
                    StatusCodes.Status404NotFound,
                    $"Snapshot {snapshotId} is not retained in catalog '{catalogName}'.",
                    "snapshot_not_found")
                : TypedResults.Ok(ToSnapshotDto(snapshot));
        }
        catch (CatalogNotFoundException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static async Task<IResult> GetSnapshotTableAsync(
        string tenantSlug,
        string catalogName,
        long snapshotId,
        string table,
        HttpContext http,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        string schema = "main",
        int limit = 100)
    {
        if (limit is < 1 or > PublicApiPagination.MaximumLimit)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                $"The limit query parameter must be an integer from 1 to {PublicApiPagination.MaximumLimit}.",
                "invalid_page_limit");
        }

        try
        {
            var principal = http.GetLakeholdPrincipal();
            var result = await lakehouse
                .ReadTableAtSnapshotAsync(
                    tenantSlug,
                    catalogName,
                    schema,
                    table,
                    snapshotId,
                    limit,
                    cancellationToken,
                    principal.TokenId,
                    QueryAuditContext.From(
                        principal,
                        http.IsLegacyApiRequest() ? QueryOrigin.Workbench : QueryOrigin.Rest))
                .ConfigureAwait(false);
            return TypedResults.Ok(QueryResponse.From(result));
        }
        catch (CatalogNotFoundException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static SnapshotDto ToSnapshotDto(SnapshotInfo snapshot)
        => new(snapshot.SnapshotId, snapshot.CommittedAt, snapshot.SchemaVersion, snapshot.CommitMessage);

    internal static async Task<Results<Ok<TableRestoreDto>, NotFound<string>, BadRequest<string>>> RestoreTableAsync(
        string tenantSlug,
        string catalogName,
        long snapshotId,
        RestoreTableRequest request,
        LakehouseService lakehouse,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrEmpty(request.Table) || string.IsNullOrEmpty(request.Schema))
        {
            return TypedResults.BadRequest("A schema and table are required.");
        }

        try
        {
            var result = await lakehouse
                .RestoreTableAsync(
                    tenantSlug,
                    catalogName,
                    request.Schema,
                    request.Table,
                    snapshotId,
                    request.Apply,
                    request.ExpectedCurrentSnapshotId,
                    cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Ok(new TableRestoreDto(
                result.Schema,
                result.Table,
                result.SnapshotId,
                result.CurrentSnapshotId,
                result.CurrentRowCount,
                result.HistoricalRowCount,
                result.RestoredColumns,
                result.CurrentOnlyColumns,
                result.HistoricalOnlyColumns,
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

    private static async Task<IResult> RunMaintenanceAsync(
        string tenantSlug,
        string catalogName,
        string operation,
        HttpContext http,
        LakehouseService lakehouse,
        PublicApiOperationStore operations,
        CancellationToken cancellationToken,
        bool apply = false)
    {
        if (!http.IsLegacyApiRequest()
            && operation is "compact" or "backup")
        {
            var queued = await operations.EnqueueAsync(
                    tenantSlug,
                    catalogName,
                    PublicApiOperationKinds.Maintenance,
                    new MaintenanceOperationRequest(operation, apply),
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(
                PublicApiRoutes.Canonical($"/operations/{queued.Id}"),
                PublicApiOperationStore.ToDto(queued));
        }

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
        catch (InvalidOperationException ex)
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

    private static async Task<IResult> RestoreBackupAsync(
        string tenantSlug,
        string catalogName,
        RestoreRequest request,
        HttpContext http,
        LakehouseService lakehouse,
        PublicApiOperationStore operations,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.TargetMetadataPath))
        {
            return TypedResults.BadRequest("A target metadata path is required.");
        }

        if (!http.IsLegacyApiRequest())
        {
            var queued = await operations.EnqueueAsync(
                    tenantSlug,
                    catalogName,
                    PublicApiOperationKinds.RestoreBackup,
                    new RestoreBackupOperationRequest(request.Generation, request.TargetMetadataPath),
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(
                PublicApiRoutes.Canonical($"/operations/{queued.Id}"),
                PublicApiOperationStore.ToDto(queued));
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

    private static async Task<IResult> EjectAsync(
        string tenantSlug,
        string catalogName,
        EjectRequest? request,
        HttpContext http,
        LakehouseService lakehouse,
        PublicApiOperationStore operations,
        CancellationToken cancellationToken)
    {
        if (!http.IsLegacyApiRequest())
        {
            var queued = await operations.EnqueueAsync(
                    tenantSlug,
                    catalogName,
                    PublicApiOperationKinds.Eject,
                    new EjectOperationRequest(request?.IncludeHistory ?? false),
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(
                PublicApiRoutes.Canonical($"/operations/{queued.Id}"),
                PublicApiOperationStore.ToDto(queued));
        }

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
        int limit = 1000,
        string? cursor = null)
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
                    Math.Clamp(limit, 1, 10_000), cursor, cancellationToken)
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
                ],
                page.NextCursor));
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

    private static async Task<IResult> StreamChangesAsync(
        string tenantSlug,
        string catalogName,
        string table,
        HttpContext http,
        LakehouseService lakehouse,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken,
        long fromSnapshot = 0,
        string schema = "main",
        long? toSnapshot = null,
        int pageSize = 1000,
        string? cursor = null)
    {
        if (pageSize is < 1 or > 10_000)
        {
            return PublicApiProblems.Create(
                http,
                StatusCodes.Status400BadRequest,
                "The pageSize query parameter must be an integer from 1 to 10000.",
                "invalid_page_limit");
        }

        try
        {
            // Freeze an omitted upper bound once, before the response begins. New commits cannot
            // move the end of a live stream and make completion nondeterministic.
            var to = toSnapshot
                ?? await lakehouse.GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken)
                    .ConfigureAwait(false)
                ?? 0;
            var first = await lakehouse
                .GetChangesAsync(
                    tenantSlug,
                    catalogName,
                    schema,
                    table,
                    fromSnapshot,
                    to,
                    pageSize,
                    cursor,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChangeNdjsonResult(
                lakehouse,
                tenantSlug,
                catalogName,
                first,
                pageSize,
                loggerFactory.CreateLogger<ChangeNdjsonResult>());
        }
        catch (CatalogNotFoundException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return PublicApiProblems.Create(http, StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static Task<Results<Ok<ChangePageDto>, NotFound<string>, BadRequest<string>>> GetSnapshotChangesAsync(
        string tenantSlug,
        string catalogName,
        long snapshot,
        string table,
        LakehouseService lakehouse,
        CancellationToken cancellationToken,
        string schema = "main",
        int limit = 1000,
        string? cursor = null)
        => GetChangesAsync(
            tenantSlug,
            catalogName,
            table,
            snapshot,
            lakehouse,
            cancellationToken,
            schema,
            snapshot,
            limit,
            cursor);

    private static async Task<Results<Ok<IReadOnlyList<SubscriptionDto>>, NotFound<string>>> ListSubscriptionsAsync(
        string tenantSlug,
        string catalogName,
        HttpContext http,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        if (!await TenantOwnsCatalogAsync(context, tenantSlug, catalogName, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        var subscriptionQuery = context.ChangeSubscriptions
            .AsNoTracking()
            .Where(s => s.Tenant.Slug == tenantSlug && s.CatalogName == catalogName)
            .OrderBy(s => s.Id);
        var subscriptions = await PublicApiPagination.ApplySourceWindow(subscriptionQuery, http)
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
        IOptions<CdcOptions> cdcOptions,
        CancellationToken cancellationToken)
    {
        if (request is null
            || !Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            return TypedResults.BadRequest("An absolute webhook endpoint URL is required.");
        }

        var destinationError = await WebhookDestinationPolicy
            .ValidateAsync(endpoint, cdcOptions.Value, cancellationToken)
            .ConfigureAwait(false);
        if (destinationError is not null)
        {
            return TypedResults.BadRequest(destinationError);
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
            PublicApiRoutes.Canonical(
                $"/tenants/{tenantSlug}/catalogs/{catalogName}/subscriptions/{subscription.Id}"),
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

    private static async Task<Results<Ok<SubscriptionDto>, NotFound<string>, BadRequest<string>>>
        UpdateSubscriptionAsync(
            string tenantSlug,
            string catalogName,
            int id,
            UpdateSubscriptionRequest request,
            ControlPlaneContext context,
            LakehouseService lakehouse,
            CancellationToken cancellationToken)
    {
        var subscription = await context.ChangeSubscriptions
            .FirstOrDefaultAsync(
                s => s.Id == id && s.Tenant.Slug == tenantSlug && s.CatalogName == catalogName,
                cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            return TypedResults.NotFound($"Subscription {id} was not found for '{tenantSlug}/{catalogName}'.");
        }

        if (request is null)
        {
            return TypedResults.BadRequest("A subscription update is required.");
        }

        if (request.Secret is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Secret) || request.Secret.Length is < 16 or > 256)
            {
                return TypedResults.BadRequest("A signing secret of 16 to 256 characters is required.");
            }

            subscription.Secret = request.Secret;
        }

        if (request.ReplayFromSnapshot is { } replay)
        {
            var latest = await lakehouse
                .GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false) ?? 0;
            if (replay <= 0 || replay > latest
                || await lakehouse.GetSnapshotAsync(
                        tenantSlug,
                        catalogName,
                        replay,
                        cancellationToken)
                    .ConfigureAwait(false) is null)
            {
                return TypedResults.BadRequest(
                    $"Replay must start at a retained snapshot between 1 and {latest}.");
            }

            var replayed = await context.ChangeDeliveries
                .Where(d => d.SubscriptionId == id && d.SnapshotId >= replay)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            context.ChangeDeliveries.RemoveRange(replayed);
            subscription.LastDeliveredSnapshot = replay - 1;
            subscription.ConsecutiveFailures = 0;
            subscription.LastAttemptUtc = null;
            subscription.LastError = null;
        }

        if (request.RetryNow)
        {
            var pending = await context.ChangeDeliveries
                .Where(d => d.SubscriptionId == id && d.DeliveredUtc == null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var delivery in pending)
            {
                delivery.NextAttemptUtc = null;
                delivery.LeaseOwner = null;
                delivery.LeaseExpiresUtc = null;
                delivery.Version++;
            }
        }

        if (request.Active is { } active)
        {
            subscription.Active = active;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(subscription));
    }

    private static async Task<Results<Ok<IReadOnlyList<CdcConsumerDto>>, NotFound<string>>> ListCdcConsumersAsync(
        string tenantSlug,
        string catalogName,
        HttpContext http,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        if (!await TenantOwnsCatalogAsync(context, tenantSlug, catalogName, cancellationToken).ConfigureAwait(false))
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        var consumerQuery = context.CdcConsumers
            .AsNoTracking()
            .Where(c => c.Tenant.Slug == tenantSlug && c.CatalogName == catalogName)
            .OrderBy(c => c.Name)
            .ThenBy(c => c.Id);
        var consumers = await PublicApiPagination.ApplySourceWindow(consumerQuery, http)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<CdcConsumerDto>>([.. consumers.Select(ToDto)]);
    }

    internal static async Task<
        Results<Ok<CdcConsumerDto>, Created<CdcConsumerDto>, NotFound<string>, BadRequest<string>>>
        RegisterCdcConsumerAsync(
            string tenantSlug,
            string catalogName,
            RegisterCdcConsumerRequest request,
            ControlPlaneContext context,
            LakehouseService lakehouse,
            CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128)
        {
            return TypedResults.BadRequest("A consumer name of 1 to 128 characters is required.");
        }

        var catalog = await context.Catalogs
            .AsNoTracking()
            .Include(c => c.Tenant)
            .FirstOrDefaultAsync(
                c => c.Tenant.Slug == tenantSlug && c.Name == catalogName,
                cancellationToken)
            .ConfigureAwait(false);
        if (catalog is null)
        {
            return TypedResults.NotFound($"Catalog '{catalogName}' was not found for tenant '{tenantSlug}'.");
        }

        var latest = await lakehouse
            .GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false) ?? 0;
        if (request.LastAppliedSnapshot < 0 || request.LastAppliedSnapshot > latest)
        {
            return TypedResults.BadRequest(
                $"The consumer checkpoint must be between 0 and the latest snapshot {latest}.");
        }

        var existing = await context.CdcConsumers
            .FirstOrDefaultAsync(
                c => c.TenantId == catalog.TenantId
                     && c.CatalogName == catalogName
                     && c.Name == request.Name,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (request.LastAppliedSnapshot < existing.LastAppliedSnapshot)
            {
                return TypedResults.BadRequest(
                    $"Consumer '{request.Name}' is already at snapshot {existing.LastAppliedSnapshot}; "
                    + "a normal checkpoint update cannot move backwards.");
            }

            existing.LastAppliedSnapshot = request.LastAppliedSnapshot;
            existing.Active = true;
            existing.UpdatedUtc = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return TypedResults.Ok(ToDto(existing));
        }

        var consumer = new CdcConsumer
        {
            TenantId = catalog.TenantId,
            CatalogName = catalogName,
            Name = request.Name,
            LastAppliedSnapshot = request.LastAppliedSnapshot,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        context.CdcConsumers.Add(consumer);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(
            PublicApiRoutes.Canonical(
                $"/tenants/{tenantSlug}/catalogs/{catalogName}/cdc/consumers/{consumer.Id}"),
            ToDto(consumer));
    }

    internal static async Task<Results<Ok<CdcConsumerDto>, NotFound<string>, BadRequest<string>>>
        AdvanceCdcConsumerAsync(
            string tenantSlug,
            string catalogName,
            int id,
            AdvanceCdcConsumerRequest request,
            ControlPlaneContext context,
            LakehouseService lakehouse,
            CancellationToken cancellationToken)
    {
        var consumer = await context.CdcConsumers
            .FirstOrDefaultAsync(
                c => c.Id == id && c.Tenant.Slug == tenantSlug && c.CatalogName == catalogName,
                cancellationToken)
            .ConfigureAwait(false);
        if (consumer is null)
        {
            return TypedResults.NotFound($"CDC consumer {id} was not found for '{tenantSlug}/{catalogName}'.");
        }

        var latest = await lakehouse
            .GetLatestSnapshotAsync(tenantSlug, catalogName, cancellationToken)
            .ConfigureAwait(false) ?? 0;
        if (request is null
            || request.LastAppliedSnapshot < consumer.LastAppliedSnapshot
            || request.LastAppliedSnapshot > latest)
        {
            return TypedResults.BadRequest(
                $"The checkpoint must be between the current value {consumer.LastAppliedSnapshot} "
                + $"and latest snapshot {latest}.");
        }

        consumer.LastAppliedSnapshot = request.LastAppliedSnapshot;
        consumer.UpdatedUtc = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(consumer));
    }

    internal static async Task<Results<NoContent, NotFound<string>>> DeleteCdcConsumerAsync(
        string tenantSlug,
        string catalogName,
        int id,
        ControlPlaneContext context,
        CancellationToken cancellationToken)
    {
        var consumer = await context.CdcConsumers
            .FirstOrDefaultAsync(
                c => c.Id == id && c.Tenant.Slug == tenantSlug && c.CatalogName == catalogName,
                cancellationToken)
            .ConfigureAwait(false);
        if (consumer is null)
        {
            return TypedResults.NotFound($"CDC consumer {id} was not found for '{tenantSlug}/{catalogName}'.");
        }

        context.CdcConsumers.Remove(consumer);
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

    private static TableStorageDto ToTableStorageDto(TableStorageInfo table, long advisoryFileSizeBytes) =>
        new(
            table.SchemaName,
            table.TableName,
            table.RowCount,
            table.InlinedRows,
            table.FileCount,
            table.FileSizeBytes,
            table.DeleteFileCount,
            table.DeleteFileSizeBytes,
            table.AverageFileSizeBytes,
            table.InlinedRows > 0,
            // One file cannot be merged with anything, so a small single file is not
            // fragmentation — it is just a small table, and advising compaction on it would train
            // the operator to ignore the signal.
            table.FileCount > 1 && table.AverageFileSizeBytes < advisoryFileSizeBytes);

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

    private static CdcConsumerDto ToDto(CdcConsumer consumer) => new(
        consumer.Id,
        consumer.Name,
        consumer.CatalogName,
        consumer.LastAppliedSnapshot,
        consumer.Active,
        consumer.CreatedUtc,
        consumer.UpdatedUtc);

    private static string ChangeTypeName(ChangeType change) => change switch
    {
        ChangeType.Insert => "insert",
        ChangeType.Delete => "delete",
        ChangeType.UpdatePreimage => "update_preimage",
        ChangeType.UpdatePostimage => "update_postimage",
        _ => "unknown",
    };

    internal static Ok<IReadOnlyList<ScheduledRunDto>> GetScheduledRuns(HttpContext http, ScheduledRunLog log)
    {
        // Same projection rule as the tenant listing: an instance token (and the transitional
        // token-less caller) sees every run, a tenant token sees only its own tenant's, and a
        // catalog-narrowed token sees only that catalog's. Reachability was the filter's decision.
        var principal = http.GetLakeholdPrincipal();
        var runs = log.Recent().AsEnumerable();

        if (principal.Scope == TokenScope.Tenant)
        {
            runs = runs.Where(r => string.Equals(r.Tenant, principal.TenantSlug, StringComparison.Ordinal));

            if (principal.CatalogName is { } catalog)
            {
                runs = runs.Where(r => string.Equals(r.Catalog, catalog, StringComparison.Ordinal));
            }
        }

        return TypedResults.Ok<IReadOnlyList<ScheduledRunDto>>(
        [
            .. runs.Select(r => new ScheduledRunDto(
                r.Job, r.Tenant, r.Catalog, r.StartedUtc, r.ElapsedMilliseconds, r.Succeeded, r.Detail)),
        ]);
    }

    private static async Task<Ok<IReadOnlyList<QueryRunDto>>> GetHistoryAsync(
        string tenantSlug,
        HttpContext http,
        ControlPlaneContext context,
        CancellationToken cancellationToken,
        int limit = 50)
    {
        var historyQuery = context.QueryRuns
            .AsNoTracking()
            .Where(r => r.Tenant.Slug == tenantSlug)
            .OrderByDescending(r => r.StartedUtc)
            .ThenByDescending(r => r.Id);
        var windowedHistory = http.IsLegacyApiRequest()
            ? historyQuery.Take(Math.Clamp(limit, 1, 200))
            : PublicApiPagination.ApplySourceWindow(historyQuery, http);
        var history = await windowedHistory
            .Select(r => new QueryRunDto(
                r.Id,
                r.CatalogName,
                r.Sql,
                r.Language,
                r.StartedUtc,
                r.ElapsedMilliseconds,
                r.RowCount,
                r.Succeeded,
                r.Error,
                r.TokenId,
                // Left join by hand: the token may have been deleted, so its name is best-effort and
                // the audit row survives without it.
                context.ApiTokens.Where(t => t.Id == r.TokenId).Select(t => t.Name).FirstOrDefault(),
                r.MemberId,
                r.ActorKind.ToString(),
                r.MemberId != null
                    ? context.TenantMembers
                        .Where(member => member.Id == r.MemberId)
                        .Select(member => member.DisplayName)
                        .FirstOrDefault()
                    : context.ApiTokens
                        .Where(token => token.Id == r.TokenId)
                        .Select(token => token.Name)
                        .FirstOrDefault(),
                r.Origin.ToString()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return TypedResults.Ok<IReadOnlyList<QueryRunDto>>(history);
    }
}

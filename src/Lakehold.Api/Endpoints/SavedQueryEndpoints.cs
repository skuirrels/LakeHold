using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Lakehold.Api.Endpoints;

/// <summary>HTTP transport for catalog-scoped saved-query use cases.</summary>
public static class SavedQueryEndpoints
{
    /// <summary>Maps saved-query authoring, execution, and view publication routes.</summary>
    public static void MapSavedQueryEndpoints(this RouteGroupBuilder tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);

        var route = "/{tenantSlug}/catalogs/{catalogName}/saved-queries";

        tenants.MapGet(route, ListAsync)
            .WithSummary("Lists reusable queries saved in a catalog.");

        tenants.MapGet(route + "/{id:int}", GetAsync)
            .WithSummary("Returns one reusable query.");

        tenants.MapPost(route, CreateAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Saves a reusable query. Requires editor or owner access.");

        tenants.MapPut(route + "/{id:int}", UpdateAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Updates a reusable query at an expected revision.");

        tenants.MapDelete(route + "/{id:int}", DeleteAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Deletes an unpublished reusable query.");

        tenants.MapPost(route + "/{id:int}/execute", ExecuteAsync)
            .WithSummary("Executes a saved query through a read-only catalog attachment.");

        tenants.MapPost(route + "/{id:int}/publish", PublishAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Publishes the current query revision as a catalog view.");

        tenants.MapPost(route + "/{id:int}/unpublish", UnpublishAsync)
            .RequireCapability(Capability.TenantWrite)
            .WithSummary("Drops the query's published view.");
    }

    internal static async Task<Results<Ok<IReadOnlyList<SavedQueryDto>>, NotFound<string>>> ListAsync(
        string tenantSlug,
        string catalogName,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var queries = await savedQueries.ListAsync(tenantSlug, catalogName, cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok<IReadOnlyList<SavedQueryDto>>([.. queries.Select(ToDto)]);
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    internal static async Task<Results<Ok<SavedQueryDto>, NotFound<string>>> GetAsync(
        string tenantSlug,
        string catalogName,
        int id,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await savedQueries.GetAsync(tenantSlug, catalogName, id, cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(ToDto(query));
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
    }

    internal static async Task<Results<Created<SavedQueryDto>, NotFound<string>, BadRequest<string>, Conflict<string>>> CreateAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        CreateSavedQueryRequest request,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await savedQueries
                .CreateAsync(
                    tenantSlug,
                    catalogName,
                    request?.Name ?? string.Empty,
                    request?.Description,
                    request?.Sql ?? string.Empty,
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);

            return TypedResults.Created(
                $"/api/tenants/{tenantSlug}/catalogs/{catalogName}/saved-queries/{query.Id}",
                ToDto(query));
        }
        catch (CatalogNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (SavedQueryValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
        catch (SavedQueryConflictException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
    }

    internal static async Task<Results<Ok<SavedQueryDto>, NotFound<string>, BadRequest<string>, Conflict<string>>> UpdateAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        int id,
        UpdateSavedQueryRequest request,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await savedQueries
                .UpdateAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    request?.Revision ?? 0,
                    request?.Name ?? string.Empty,
                    request?.Description,
                    request?.Sql ?? string.Empty,
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(ToDto(query));
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (SavedQueryValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
        catch (SavedQueryConflictException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
    }

    internal static async Task<Results<NoContent, NotFound<string>, Conflict<string>>> DeleteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int revision,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            await savedQueries.DeleteAsync(tenantSlug, catalogName, id, revision, cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.NoContent();
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (SavedQueryConflictException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
    }

    internal static async Task<Results<Ok<QueryResponse>, NotFound<string>, BadRequest<string>>> ExecuteAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        int id,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var principal = http.GetLakeholdPrincipal();
            var result = await savedQueries
                .ExecuteAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    principal.TokenId,
                    recordHistory: !principal.IsDemo,
                    cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(QueryResponse.From(result));
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    internal static async Task<Results<Ok<SavedQueryDto>, NotFound<string>, BadRequest<string>, Conflict<string>>> PublishAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        int id,
        PublishSavedQueryRequest request,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await savedQueries
                .PublishAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    request?.Revision ?? 0,
                    request?.Schema ?? string.Empty,
                    request?.ViewName ?? string.Empty,
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(ToDto(query));
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (SavedQueryValidationException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
        catch (SavedQueryConflictException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    internal static async Task<Results<Ok<SavedQueryDto>, NotFound<string>, BadRequest<string>, Conflict<string>>> UnpublishAsync(
        HttpContext http,
        string tenantSlug,
        string catalogName,
        int id,
        int revision,
        SavedQueryService savedQueries,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = await savedQueries
                .UnpublishAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    revision,
                    http.GetLakeholdPrincipal().TokenId,
                    cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(ToDto(query));
        }
        catch (SavedQueryNotFoundException ex)
        {
            return TypedResults.NotFound(ex.Message);
        }
        catch (SavedQueryConflictException ex)
        {
            return TypedResults.Conflict(ex.Message);
        }
        catch (DuckDB.NET.Data.DuckDBException ex)
        {
            return TypedResults.BadRequest(ex.Message);
        }
    }

    private static SavedQueryDto ToDto(SavedQuery query) => new(
        query.Id,
        query.Name,
        query.Description,
        query.Sql,
        query.Revision,
        query.CreatedUtc,
        query.UpdatedUtc,
        query.CreatedByTokenId,
        query.UpdatedByTokenId,
        query.PublishedSchema,
        query.PublishedViewName,
        query.PublishedRevision,
        query.PublishedUtc);
}

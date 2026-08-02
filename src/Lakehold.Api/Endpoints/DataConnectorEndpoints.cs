using Lakehold.Api.Auth;
using Lakehold.Api.Connectors;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Endpoints;

/// <summary>Thin HTTP administration for durable managed connectors and their run history.</summary>
public static class DataConnectorEndpoints
{
    public static void MapDataConnectorEndpoints(this RouteGroupBuilder tenants)
    {
        const string route = "/{tenantSlug}/catalogs/{catalogName}/connectors";
        tenants.MapGet(route, ListAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapGet(route + "/{id:int}", GetAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapPost(route, CreateAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapPut(route + "/{id:int}", UpdateAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapDelete(route + "/{id:int}", DeleteAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapPost(route + "/{id:int}/run", RunAsync).RequireCapability(Capability.TenantOwner);
        tenants.MapGet(route + "/{id:int}/runs", ListRunsAsync).RequireCapability(Capability.TenantOwner);
    }

    private static async Task<IResult> ListAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        var items = await connectors.ListAsync(tenantSlug, catalogName, cancellationToken).ConfigureAwait(false);
        return Results.Ok(items.Select(DataConnectorDto.From));
    }

    private static async Task<IResult> GetAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(DataConnectorDto.From(
                await connectors.GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false)));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<IResult> CreateAsync(
        string tenantSlug,
        string catalogName,
        DataConnectorDefinitionRequest request,
        DataConnectorService connectors,
        IOptions<ConnectorOptions> options,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request, options.Value, cancellationToken).ConfigureAwait(false);
        if (validation.Error is not null)
        {
            return Results.BadRequest(validation.Error);
        }

        try
        {
            var connector = await connectors.CreateAsync(
                    tenantSlug,
                    catalogName,
                    validation.Definition!,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Created(
                $"/api/tenants/{tenantSlug}/catalogs/{catalogName}/connectors/{connector.Id}",
                DataConnectorDto.From(connector));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> UpdateAsync(
        string tenantSlug,
        string catalogName,
        int id,
        UpdateDataConnectorRequest request,
        DataConnectorService connectors,
        IOptions<ConnectorOptions> options,
        CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(request.Definition, options.Value, cancellationToken).ConfigureAwait(false);
        if (validation.Error is not null)
        {
            return Results.BadRequest(validation.Error);
        }

        try
        {
            var connector = await connectors.UpdateAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    request.Version,
                    validation.Definition!,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(DataConnectorDto.From(connector));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            await connectors.DeleteAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
        catch (DataConnectorConflictException ex)
        {
            return Results.Conflict(ex.Message);
        }
    }

    private static async Task<IResult> RunAsync(
        string tenantSlug,
        string catalogName,
        int id,
        DataConnectorService connectors,
        ConnectorRunner runner,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await connectors.GetAsync(tenantSlug, catalogName, id, cancellationToken).ConfigureAwait(false);
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }

        var result = await runner.RunAsync(id, DataConnectorTrigger.Manual, cancellationToken).ConfigureAwait(false);
        return result is null
            ? Results.Conflict("The connector is already refreshing on another worker node.")
            : ToHttpResult(result);
    }

    internal static IResult ToHttpResult(ConnectorExecutionResult result)
    {
        var response = new DataConnectorExecutionDto(
                result.RunId,
                result.Status,
                result.RowsRead,
                result.RowsPublished,
                result.SourceVersion,
                result.Error);
        return result.FailureKind switch
        {
            ConnectorExecutionFailureKind.None => Results.Ok(response),
            ConnectorExecutionFailureKind.ClaimConflict => Results.Conflict(response),
            ConnectorExecutionFailureKind.Quality => Results.Json(response, statusCode: StatusCodes.Status422UnprocessableEntity),
            ConnectorExecutionFailureKind.TargetConflict => Results.Conflict(response),
            ConnectorExecutionFailureKind.Capacity => Results.Json(
                response,
                statusCode: StatusCodes.Status503ServiceUnavailable),
            ConnectorExecutionFailureKind.SourceOrImport => Results.Json(response, statusCode: StatusCodes.Status502BadGateway),
            ConnectorExecutionFailureKind.PublicationState => Results.Json(
                response,
                statusCode: StatusCodes.Status500InternalServerError),
            _ => throw new InvalidOperationException("Unknown connector execution outcome."),
        };
    }

    private static async Task<IResult> ListRunsAsync(
        string tenantSlug,
        string catalogName,
        int id,
        int? limit,
        DataConnectorService connectors,
        CancellationToken cancellationToken)
    {
        try
        {
            var runs = await connectors.ListRunsAsync(
                    tenantSlug,
                    catalogName,
                    id,
                    limit ?? 50,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(runs.Select(DataConnectorRunDto.From));
        }
        catch (CatalogNotFoundException ex)
        {
            return Results.NotFound(ex.Message);
        }
    }

    private static async Task<(DataConnectorDefinition? Definition, string? Error)> ValidateAsync(
        DataConnectorDefinitionRequest request,
        ConnectorOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Kind)
            || request.Kind.All(char.IsDigit)
            || !Enum.TryParse<DataConnectorKind>(request.Kind, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            return (null, "Connector kind must be 'rest' or 'grpc'.");
        }

        var format = request.RestResponseFormat?.Trim().ToLowerInvariant() switch
        {
            null or "" or "json-array" => RestResponseFormat.JsonArray,
            "ndjson" => RestResponseFormat.NewlineDelimitedJson,
            _ => (RestResponseFormat?)null,
        };
        if (format is null)
        {
            return (null, "REST response format must be 'json-array' or 'ndjson'.");
        }

        if (!Uri.TryCreate(request.EndpointUrl, UriKind.Absolute, out var endpoint))
        {
            return (null, "A connector endpoint must be an absolute URL.");
        }

        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                endpoint,
                options,
                "Connector",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            return (null, resolution.Error);
        }

        try
        {
            _ = SqlIdentifier.Quote(request.TargetSchema);
            _ = SqlIdentifier.Quote(request.TargetTable);
            foreach (var column in (request.RequiredColumns ?? []).Concat(request.NotNullColumns ?? []))
            {
                _ = SqlIdentifier.Quote(column);
            }
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        return (new DataConnectorDefinition(
            request.Name,
            request.Description,
            request.Owner,
            request.Tags ?? [],
            kind,
            endpoint.AbsoluteUri,
            request.CredentialEnvironmentVariable,
            format.Value,
            request.TargetSchema,
            request.TargetTable,
            request.MinimumRows,
            request.RequiredColumns ?? [],
            request.NotNullColumns ?? [],
            request.Enabled,
            request.RefreshIntervalSeconds), null);
    }
}

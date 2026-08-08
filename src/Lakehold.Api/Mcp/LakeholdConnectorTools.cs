using System.ComponentModel;
using Lakehold.Api.Connectors;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Mcp;

/// <summary>
/// Administrator connector control plane.  These tools deliberately share the endpoint validation
/// and application service with the browser: MCP-created connectors are ordinary durable catalog
/// configuration, not an agent-only side channel.
/// </summary>
[McpServerToolType]
public sealed class LakeholdConnectorTools(
    DataConnectorService connectors,
    DataConnectorSourceResolver sources,
    ConnectorExecutionService executions,
    IOptions<ConnectorOptions> options,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "list_connectors", Title = "List managed connectors", ReadOnly = true, Destructive = false)]
    [Description("Lists durable managed connector definitions for a catalog. Requires tenant administrator access.")]
    public async Task<IReadOnlyList<DataConnectorDto>> ListAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        var items = await connectors.ListAsync(tenant, catalog, cancellationToken).ConfigureAwait(false);
        return items.Select(DataConnectorDto.From).ToArray();
    }

    [McpServerTool(Name = "get_connector", Title = "Get managed connector", ReadOnly = true, Destructive = false)]
    [Description("Returns one saved connector definition, including non-secret source settings and its optimistic version.")]
    public async Task<DataConnectorDto> GetAsync(string tenant, string catalog, int id, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        try
        {
            return DataConnectorDto.From(await connectors.GetAsync(tenant, catalog, id, cancellationToken).ConfigureAwait(false));
        }
        catch (CatalogNotFoundException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "validate_connector", Title = "Validate managed connector", ReadOnly = true, Destructive = false)]
    [Description("Validates a connector definition without saving it. Requires tenant administrator access.")]
    public async Task<ConnectorValidationResult> ValidateAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector definition. Secret references only; never secret values.")] DataConnectorDefinitionRequest definition,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        var validation = await DataConnectorEndpoints.ValidateAsync(
                tenant, catalog, definition, sources, options.Value, cancellationToken)
            .ConfigureAwait(false);
        return new ConnectorValidationResult(validation.Error is null, validation.Error);
    }

    [McpServerTool(Name = "create_connector", Title = "Create managed connector", ReadOnly = false, Destructive = false)]
    [Description("Creates a durable connector definition visible in LakeHold administration. Requires tenant administrator access.")]
    public async Task<DataConnectorDto> CreateAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector definition. Secret references only; never secret values.")] DataConnectorDefinitionRequest definition,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        var validated = await RequireDefinitionAsync(tenant, catalog, definition, cancellationToken).ConfigureAwait(false);
        try
        {
            return DataConnectorDto.From(await connectors.CreateAsync(
                    tenant, catalog, validated, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DataConnectorConflictException or CatalogNotFoundException)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "update_connector", Title = "Update managed connector", ReadOnly = false, Destructive = false)]
    [Description("Updates a connector using its optimistic version. The result is immediately visible in LakeHold administration.")]
    public async Task<DataConnectorDto> UpdateAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier.")] int id,
        [Description("Current connector version from list_connectors or a previous save.")] int version,
        [Description("Replacement connector definition. Secret references only; never secret values.")] DataConnectorDefinitionRequest definition,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        var validated = await RequireDefinitionAsync(tenant, catalog, definition, cancellationToken).ConfigureAwait(false);
        try
        {
            return DataConnectorDto.From(await connectors.UpdateAsync(
                    tenant, catalog, id, version, validated, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or DataConnectorConflictException or CatalogNotFoundException)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "retire_connector", Title = "Retire managed connector", ReadOnly = false, Destructive = true)]
    [Description("Retires a connector. It is no longer scheduled, while its run lineage remains retained.")]
    public async Task RetireAsync(string tenant, string catalog, int id, int version, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        try
        {
            await connectors.DeleteAsync(tenant, catalog, id, version, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CatalogNotFoundException or DataConnectorConflictException)
        {
            throw new McpException(exception.Message);
        }
    }

    // Destructive: a full-snapshot connector replaces the whole of its DuckLake target, so a run is
    // not an additive operation the client should wave through without asking.
    [McpServerTool(Name = "run_connector", Title = "Run managed connector", ReadOnly = false, Destructive = true)]
    [Description("Runs one enabled connector now and returns its safe execution result. A full-snapshot connector replaces its target table.")]
    public Task<DataConnectorExecutionDto> RunAsync(string tenant, string catalog, int id, CancellationToken cancellationToken) =>
        RunAsync(tenant, catalog, id, retry: false, cancellationToken);

    [McpServerTool(Name = "retry_connector", Title = "Retry managed connector", ReadOnly = false, Destructive = true)]
    [Description("Retries a failed connector using its existing definition, replaying its bounded source window.")]
    public Task<DataConnectorExecutionDto> RetryAsync(string tenant, string catalog, int id, int version, CancellationToken cancellationToken) =>
        RunAsync(tenant, catalog, id, retry: true, version, cancellationToken);

    [McpServerTool(Name = "pause_connector", Title = "Pause managed connector", ReadOnly = false, Destructive = false)]
    [Description("Pauses a connector using its current optimistic version.")]
    public Task<DataConnectorDto> PauseAsync(string tenant, string catalog, int id, int version, CancellationToken cancellationToken) =>
        ChangeStateAsync(tenant, catalog, () => connectors.PauseAsync(tenant, catalog, id, version, DateTimeOffset.UtcNow, cancellationToken));

    [McpServerTool(Name = "resume_connector", Title = "Resume managed connector", ReadOnly = false, Destructive = false)]
    [Description("Resumes a connector using its current optimistic version.")]
    public Task<DataConnectorDto> ResumeAsync(string tenant, string catalog, int id, int version, CancellationToken cancellationToken) =>
        ChangeStateAsync(tenant, catalog, () => connectors.ResumeAsync(
            tenant, catalog, id, version, resetFailures: false, now: DateTimeOffset.UtcNow, cancellationToken));

    [McpServerTool(Name = "list_connector_runs", Title = "List managed connector runs", ReadOnly = true, Destructive = false)]
    [Description("Lists recent safe run history for a connector.")]
    public async Task<IReadOnlyList<DataConnectorRunDto>> ListRunsAsync(string tenant, string catalog, int id, int limit, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        try
        {
            var runs = await connectors.ListRunsAsync(tenant, catalog, id, Math.Clamp(limit, 1, 200), cancellationToken).ConfigureAwait(false);
            return runs.Select(DataConnectorRunDto.From).ToArray();
        }
        catch (CatalogNotFoundException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    [McpServerTool(Name = "list_connector_dead_letters", Title = "List connector dead letters", ReadOnly = true, Destructive = false)]
    [Description("Lists recent dead-lettered run evidence for a connector.")]
    public async Task<IReadOnlyList<DataConnectorRunDto>> ListDeadLettersAsync(string tenant, string catalog, int id, int limit, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        try
        {
            var runs = await connectors.ListRunsAsync(tenant, catalog, id, DataConnectorRunStatus.DeadLettered, Math.Clamp(limit, 1, 200), cancellationToken).ConfigureAwait(false);
            return runs.Select(DataConnectorRunDto.From).ToArray();
        }
        catch (CatalogNotFoundException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    private async Task<DataConnectorDefinition> RequireDefinitionAsync(string tenant, string catalog, DataConnectorDefinitionRequest request, CancellationToken cancellationToken)
    {
        var validation = await DataConnectorEndpoints.ValidateAsync(tenant, catalog, request, sources, options.Value, cancellationToken).ConfigureAwait(false);
        if (validation.Error is not null || validation.Definition is null)
        {
            throw new McpException(validation.Error ?? "Connector definition is invalid.");
        }

        return validation.Definition;
    }

    private async Task<DataConnectorExecutionDto> RunAsync(string tenant, string catalog, int id, bool retry, CancellationToken cancellationToken) =>
        await RunAsync(tenant, catalog, id, retry, version: null, cancellationToken).ConfigureAwait(false);

    private async Task<DataConnectorExecutionDto> RunAsync(string tenant, string catalog, int id, bool retry, int? version, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        try
        {
            var connector = await connectors.GetAsync(tenant, catalog, id, cancellationToken).ConfigureAwait(false);
            if (!retry && connector.SourceAcknowledgementPendingUtc is not null)
            {
                throw new McpException(
                    "The last published batch is awaiting source acknowledgement. Use retry_connector with the current version to recover and replay it safely.");
            }
            if (retry)
            {
                if (version is null)
                {
                    throw new McpException("Retry requires the current connector version.");
                }

                _ = await connectors.ResumeAsync(
                        tenant, catalog, id, version.Value, resetFailures: true,
                        now: DateTimeOffset.UtcNow, cancellationToken)
                    .ConfigureAwait(false);
            }
            return await executions.RunAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new McpException("The connector is already refreshing on another worker node.");
        }
        catch (CatalogNotFoundException exception)
        {
            throw new McpException(exception.Message);
        }
    }

    private async Task<DataConnectorDto> ChangeStateAsync(string tenant, string catalog, Func<Task<DataConnector>> operation)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        try
        {
            return DataConnectorDto.From(await operation().ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is CatalogNotFoundException or DataConnectorConflictException or InvalidOperationException)
        {
            throw new McpException(exception.Message);
        }
    }
}

public sealed record ConnectorValidationResult(bool Valid, string? Error);

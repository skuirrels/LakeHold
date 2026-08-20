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
/// <remarks>
///     All twelve declare <c>Capability.TenantOwner</c>, the same capability the HTTP routes declare,
///     enforced by the same policy (invariant 21). The seven mutating ones additionally sit behind
///     Allow writes.
/// </remarks>
[McpServerToolType]
public sealed class LakeholdConnectorTools(
    DataConnectorService connectors,
    DataConnectorSourceResolver sources,
    ConnectorExecutionService executions,
    IOptions<ConnectorOptions> options,
    TimeProvider clock,
    IHttpContextAccessor httpContextAccessor)
{
    [McpServerTool(Name = "list_connectors", Title = "List managed connectors", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists durable managed connector definitions for a catalog, with each one's current version "
        + "and schedule state. Requires tenant administrator access.")]
    public Task<IReadOnlyList<DataConnectorDto>> ListAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync<IReadOnlyList<DataConnectorDto>>(async () =>
            [.. (await connectors.ListAsync(tenant, catalog, cancellationToken).ConfigureAwait(false))
                .Select(DataConnectorDto.From)]);
    }

    [McpServerTool(Name = "get_connector", Title = "Get managed connector", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Returns one saved connector definition, including non-secret source settings and its "
        + "optimistic version. Secret values are never returned, only the references an operator bound.")]
    public Task<DataConnectorDto> GetAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync(async () => DataConnectorDto.From(
            await connectors.GetAsync(tenant, catalog, id, cancellationToken).ConfigureAwait(false)));
    }

    [McpServerTool(Name = "validate_connector", Title = "Validate managed connector", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Checks a connector definition against the same validation the administration UI applies, "
        + "without saving it. Use this before create_connector to see the error without a failed write.")]
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

    [McpServerTool(Name = "create_connector", Title = "Create managed connector", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Creates a durable connector definition visible in LakeHold administration. Requires tenant "
        + "administrator access. Secret references only; never secret values.")]
    public Task<DataConnectorDto> CreateAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector definition. Secret references only; never secret values.")] DataConnectorDefinitionRequest definition,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardStatefulAsync(async () =>
        {
            var validated = await RequireDefinitionAsync(tenant, catalog, definition, cancellationToken)
                .ConfigureAwait(false);
            return DataConnectorDto.From(await connectors
                .CreateAsync(tenant, catalog, validated, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false));
        });
    }

    [McpServerTool(Name = "update_connector", Title = "Update managed connector", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Replaces a connector definition using its optimistic version. A version mismatch means "
        + "someone else changed it, so re-read it with get_connector rather than retrying.")]
    public Task<DataConnectorDto> UpdateAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier.")] int id,
        [Description("Current connector version from list_connectors or a previous save.")] int version,
        [Description("Replacement connector definition. Secret references only; never secret values.")] DataConnectorDefinitionRequest definition,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardStatefulAsync(async () =>
        {
            var validated = await RequireDefinitionAsync(tenant, catalog, definition, cancellationToken)
                .ConfigureAwait(false);
            return DataConnectorDto.From(await connectors
                .UpdateAsync(tenant, catalog, id, version, validated, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false));
        });
    }

    [McpServerTool(Name = "retire_connector", Title = "Retire managed connector", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description(
        "Retires a connector so it is no longer scheduled. Its definition and run lineage are retained, "
        + "so this is reversible in the sense that the history survives, but it stops all ingestion.")]
    public Task RetireAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        [Description("Current connector version from list_connectors.")] int version,
        CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync(() =>
            connectors.DeleteAsync(tenant, catalog, id, version, cancellationToken));
    }

    // Destructive: a full-snapshot connector replaces the whole of its DuckLake target, so a run is
    // not an additive operation the client should wave through without asking.
    [McpServerTool(Name = "run_connector", Title = "Run managed connector", ReadOnly = false, Destructive = true, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Runs one enabled connector now and returns its safe execution result. A full-snapshot "
        + "connector REPLACES its target table; an incremental one performs a keyed upsert.")]
    public Task<DataConnectorExecutionDto> RunAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        CancellationToken cancellationToken) =>
        RunAsync(tenant, catalog, id, retry: false, version: null, cancellationToken);

    [McpServerTool(Name = "retry_connector", Title = "Retry managed connector", ReadOnly = false, Destructive = true, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Retries a failed connector using its existing definition, replaying its bounded source window. "
        + "This is the recovery path when a run left a batch awaiting source acknowledgement.")]
    public Task<DataConnectorExecutionDto> RetryAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        [Description("Current connector version from list_connectors.")] int version,
        CancellationToken cancellationToken) =>
        RunAsync(tenant, catalog, id, retry: true, version, cancellationToken);

    [McpServerTool(Name = "pause_connector", Title = "Pause managed connector", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description("Stops a connector being scheduled, leaving its definition and checkpoints intact.")]
    public Task<DataConnectorDto> PauseAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        [Description("Current connector version from list_connectors.")] int version,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(tenant, catalog, () => connectors.PauseAsync(
            tenant, catalog, id, version, clock.GetUtcNow(), cancellationToken));

    [McpServerTool(Name = "resume_connector", Title = "Resume managed connector", ReadOnly = false, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Returns a paused connector to its schedule. Does not clear a failure count — use "
        + "retry_connector to recover a failed one.")]
    public Task<DataConnectorDto> ResumeAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        [Description("Current connector version from list_connectors.")] int version,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(tenant, catalog, () => connectors.ResumeAsync(
            tenant, catalog, id, version, resetFailures: false, now: clock.GetUtcNow(), cancellationToken));

    [McpServerTool(Name = "list_connector_runs", Title = "List managed connector runs", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists recent run history for a connector, newest first: rows read and published, quality "
        + "outcome, source version, and any sanitised error.")]
    public Task<IReadOnlyList<DataConnectorRunDto>> ListRunsAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        CancellationToken cancellationToken,
        [Description("Maximum runs to return, from 1 to 200. Defaults to 20.")] int limit = 20)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync<IReadOnlyList<DataConnectorRunDto>>(async () =>
            [.. (await connectors
                    .ListRunsAsync(tenant, catalog, id, Math.Clamp(limit, 1, 200), cancellationToken)
                    .ConfigureAwait(false))
                .Select(DataConnectorRunDto.From)]);
    }

    [McpServerTool(Name = "list_connector_dead_letters", Title = "List connector dead letters", ReadOnly = true, Destructive = false, UseStructuredContent = true, OpenWorld = false)]
    [Description(
        "Lists runs that exhausted their retries and were dead-lettered, newest first. This is where "
        + "to look first when a connector has silently stopped producing data.")]
    public Task<IReadOnlyList<DataConnectorRunDto>> ListDeadLettersAsync(
        [Description("Tenant slug.")] string tenant,
        [Description("Catalog name.")] string catalog,
        [Description("Connector identifier, from list_connectors.")] int id,
        CancellationToken cancellationToken,
        [Description("Maximum dead-lettered runs to return, from 1 to 200. Defaults to 20.")] int limit = 20)
    {
        McpCaller.AuthorizeOwner(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardAsync<IReadOnlyList<DataConnectorRunDto>>(async () =>
            [.. (await connectors
                    .ListRunsAsync(
                        tenant, catalog, id, DataConnectorRunStatus.DeadLettered,
                        Math.Clamp(limit, 1, 200), cancellationToken)
                    .ConfigureAwait(false))
                .Select(DataConnectorRunDto.From)]);
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

    private Task<DataConnectorExecutionDto> RunAsync(string tenant, string catalog, int id, bool retry, int? version, CancellationToken cancellationToken)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardStatefulAsync(async () =>
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
                        now: clock.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
            return await executions.RunAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new McpException("The connector is already refreshing on another worker node.");
        });
    }

    private Task<DataConnectorDto> ChangeStateAsync(string tenant, string catalog, Func<Task<DataConnector>> operation)
    {
        McpCaller.AuthorizeOwnerForWrite(httpContextAccessor, tenant, catalog);
        return McpFailure.GuardStatefulAsync(async () =>
            DataConnectorDto.From(await operation().ConfigureAwait(false)));
    }
}

/// <summary>Whether a proposed connector definition would be accepted, and why not if it would not.</summary>
public sealed record ConnectorValidationResult(bool Valid, string? Error);

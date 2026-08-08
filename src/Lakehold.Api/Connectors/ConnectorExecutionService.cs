using Lakehold.ControlPlane.Model;

namespace Lakehold.Api.Connectors;

/// <summary>
/// Public application boundary for initiating a connector run from a non-HTTP transport such as
/// MCP. It keeps the runner and its filesystem-only scratch implementation internal.
/// </summary>
public sealed class ConnectorExecutionService(IServiceProvider services)
{
    public async Task<DataConnectorExecutionDto?> RunAsync(int connectorId, CancellationToken cancellationToken)
    {
        var runner = services.GetRequiredService<ConnectorRunner>();
        var result = await runner.RunAsync(connectorId, DataConnectorTrigger.Manual, cancellationToken)
            .ConfigureAwait(false);
        return result is null
            ? null
            : new DataConnectorExecutionDto(
                result.RunId,
                result.Status,
                result.RowsRead,
                result.RowsPublished,
                result.SourceVersion,
                result.Error);
    }
}

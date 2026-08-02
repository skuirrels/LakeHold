using Lakehold.ControlPlane.Model;

namespace Lakehold.Api.Connectors;

/// <summary>Infrastructure adapter that streams one full source snapshot into bounded NDJSON.</summary>
internal interface IDataConnectorSource
{
    DataConnectorKind Kind { get; }

    Task<ConnectorSourceResult> ReadAsync(
        DataConnector connector,
        ConnectorSnapshotFile destination,
        CancellationToken cancellationToken);
}

internal sealed record ConnectorSourceResult(long RowsRead, string? SourceVersion);

/// <summary>Selects a protocol adapter without leaking transport decisions into orchestration.</summary>
internal sealed class DataConnectorSourceResolver(IEnumerable<IDataConnectorSource> sources)
{
    private readonly Dictionary<DataConnectorKind, IDataConnectorSource> _sources = sources
        .ToDictionary(source => source.Kind);

    public IDataConnectorSource Resolve(DataConnectorKind kind) =>
        _sources.TryGetValue(kind, out var source)
            ? source
            : throw new NotSupportedException($"Connector kind '{kind}' is not registered.");
}

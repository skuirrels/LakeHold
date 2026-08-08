using Lakehold.ControlPlane.Model;

namespace Lakehold.Api.Connectors;

/// <summary>Immutable capabilities exposed by a versioned connector adapter.</summary>
public sealed record ConnectorAdapterManifest(
    string Id,
    int Version,
    DataConnectorKind Kind,
    IReadOnlySet<DataConnectorReadMode> ReadModes,
    IReadOnlySet<DataConnectorAuthenticationKind> AuthenticationKinds,
    bool SupportsSourceVersion,
    int ManifestVersion = 1);

/// <summary>
///     Context supplied to an adapter. Tenant and catalog are explicit security subjects rather than
///     inferred from optional EF navigation state; the checkpoint is the last durably published cursor.
/// </summary>
public sealed record ConnectorReadContext(
    DataConnector Connector,
    string? Checkpoint,
    string TenantSlug,
    string CatalogName);

/// <summary>Source evidence and a proposed cursor. The runtime commits the cursor, not the adapter.</summary>
public sealed record ConnectorSourceResult(
    string? SourceVersion,
    string? ProposedCheckpoint = null,
    string? ReplayKey = null);

/// <summary>Infrastructure adapter that translates one protocol into bounded NDJSON records.</summary>
public interface IDataConnectorRecordWriter
{
    long Rows { get; }

    Task WriteAsync(string json, CancellationToken cancellationToken);

    void RecordSourceVersion(string? sourceVersion);
}

/// <summary>Versioned adapter SDK contract implemented by built-in and operator-provided adapters.</summary>
public interface IDataConnectorSource
{
    ConnectorAdapterManifest Manifest { get; }

    Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken);
}

/// <summary>Optional transport acknowledgement performed only after LakeHold durably publishes a batch.</summary>
internal interface IConnectorPostPublicationAcknowledger
{
    Task AcknowledgePublishedAsync(CancellationToken cancellationToken);
    Task AbandonAsync();
}

/// <summary>Selects a protocol adapter without leaking transport decisions into orchestration.</summary>
public sealed class DataConnectorSourceResolver(IEnumerable<IDataConnectorSource> sources)
{
    private readonly Dictionary<(string Id, int Version), IDataConnectorSource> _sources = sources
        .ToDictionary(source => (source.Manifest.Id, source.Manifest.Version));

    public ConnectorAdapterManifest? FindManifest(string id, int version) =>
        _sources.TryGetValue((id, version), out var source) ? source.Manifest : null;

    public IDataConnectorSource Resolve(DataConnector connector)
    {
        if (!_sources.TryGetValue((connector.AdapterId, connector.AdapterVersion), out var source))
        {
            throw new NotSupportedException(
                $"Connector adapter '{connector.AdapterId}' version {connector.AdapterVersion} is not registered.");
        }

        if (source.Manifest.Kind != connector.Kind || !source.Manifest.ReadModes.Contains(connector.ReadMode))
        {
            throw new NotSupportedException(
                $"Connector adapter '{connector.AdapterId}' does not support the requested connector contract.");
        }

        if (source.Manifest.ManifestVersion != 1
            || !source.Manifest.AuthenticationKinds.Contains(connector.Authentication().Kind))
        {
            throw new NotSupportedException(
                $"Connector adapter '{connector.AdapterId}' has an incompatible manifest or authentication contract.");
        }

        return source;
    }
}

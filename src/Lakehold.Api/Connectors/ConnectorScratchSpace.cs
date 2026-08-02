using Lakehold.Api.Importing;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>Raised when connector scratch capacity is unavailable on this worker node.</summary>
internal sealed class ConnectorScratchCapacityException(string message) : Exception(message);

/// <summary>Connector-specific facade over LakeHold's shared node-local scratch coordinator.</summary>
internal sealed class ConnectorScratchSpace : IDisposable
{
    private readonly NodeScratchSpace _inner;

    public ConnectorScratchSpace(IOptions<ConnectorOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _inner = new NodeScratchSpace(
            new NodeScratchOptions(
                settings.ScratchRoot,
                "lakehold-connectors",
                "*.ndjson",
                ".ndjson",
                settings.MaxSnapshotBytes,
                settings.MaxAggregateScratchBytes,
                settings.MaxConcurrentRuns,
                settings.MinimumFreeBytes,
                settings.StaleFileAge,
                "Lakehold:Connectors"),
            clock,
            message => new ConnectorScratchCapacityException(message));
    }

    public string ScratchRoot => _inner.ScratchRoot;

    public Task<NodeScratchLease> AcquireAsync(CancellationToken cancellationToken) =>
        _inner.AcquireAsync(contentLength: null, cancellationToken);

    public void Dispose() => _inner.Dispose();
}

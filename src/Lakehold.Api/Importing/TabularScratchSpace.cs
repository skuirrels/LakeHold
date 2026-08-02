using Microsoft.Extensions.Options;

namespace Lakehold.Api.Importing;

/// <summary>Raised when this node cannot safely reserve scratch capacity for another upload.</summary>
public sealed class TabularScratchCapacityException(string message) : Exception(message);

/// <summary>Upload-specific facade over LakeHold's shared node-local scratch coordinator.</summary>
public sealed class TabularScratchSpace : IDisposable
{
    private readonly NodeScratchSpace _inner;

    public TabularScratchSpace(IOptions<CsvUploadOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _inner = new NodeScratchSpace(
            new NodeScratchOptions(
                settings.ScratchRoot,
                "lakehold-csv-imports",
                "*.upload",
                ".upload",
                settings.MaxBytes,
                settings.MaxAggregateScratchBytes,
                settings.MaxConcurrentUploads,
                settings.MinimumFreeBytes,
                settings.StaleFileAge,
                "Lakehold:CsvImport"),
            clock,
            message => new TabularScratchCapacityException(message));

        // Older releases used .csv for scratch. Remove those crash orphans during the compatibility
        // window without widening the shared coordinator's ownership pattern.
        RemoveLegacyStaleCsv(settings.StaleFileAge, clock);
    }

    public string ScratchRoot => _inner.ScratchRoot;

    public async Task<TabularScratchLease> AcquireAsync(
        long? contentLength,
        CancellationToken cancellationToken) => new(
        await _inner.AcquireAsync(contentLength, cancellationToken).ConfigureAwait(false));

    public void Dispose() => _inner.Dispose();

    private void RemoveLegacyStaleCsv(TimeSpan staleFileAge, TimeProvider clock)
    {
        var cutoff = clock.GetUtcNow() - staleFileAge;
        foreach (var file in Directory.EnumerateFiles(ScratchRoot, "*.csv", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Real free-space checks still account for retained legacy files.
            }
        }
    }
}

/// <summary>An active node-local upload scratch reservation.</summary>
public sealed class TabularScratchLease : IAsyncDisposable
{
    private readonly NodeScratchLease _inner;

    internal TabularScratchLease(NodeScratchLease inner) => _inner = inner;

    public string Path => _inner.Path;

    public long ReservedBytes => _inner.ReservedBytes;

    public FileStream OpenWrite() => _inner.OpenWrite();

    public void EnsureReserved(long requiredBytes) => _inner.EnsureReserved(requiredBytes);

    public void RecordWritten(int bytes) => _inner.RecordWritten(bytes);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}

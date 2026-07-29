using Microsoft.Extensions.Options;

namespace Lakehold.Api.Importing;

/// <summary>Raised when this node cannot safely reserve scratch capacity for another upload.</summary>
public sealed class CsvScratchCapacityException(string message) : Exception(message);

/// <summary>
///     Coordinates disposable CSV scratch files across every request handled by one application
///     node.
/// </summary>
/// <remarks>
///     Scratch remains deliberately node-local because one request writes and consumes it. The
///     coordinator adds the missing node-wide invariants: bounded concurrency, bounded aggregate
///     reservations, a free-space floor, and cleanup of files abandoned by an earlier process.
/// </remarks>
public sealed class CsvScratchSpace : IDisposable
{
    private readonly CsvUploadOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _capacityLock = new();
    private long _reservedBytes;
    private long _unwrittenReservedBytes;
    private bool _disposed;

    public CsvScratchSpace(IOptions<CsvUploadOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options.Value;
        _clock = clock;
        ValidateOptions(_options);

        ScratchRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(_options.ScratchRoot)
                ? Path.Combine(Path.GetTempPath(), "lakehold-csv-imports")
                : _options.ScratchRoot);
        Directory.CreateDirectory(ScratchRoot);
        RemoveStaleFiles();

        _concurrency = new SemaphoreSlim(
            _options.MaxConcurrentUploads,
            _options.MaxConcurrentUploads);
    }

    /// <summary>The fully resolved LakeHold-owned scratch directory.</summary>
    public string ScratchRoot { get; }

    /// <summary>Reserves one upload slot and its declared size, when known.</summary>
    public async Task<CsvScratchLease> AcquireAsync(
        long? contentLength,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        var reserved = contentLength is > 0 ? contentLength.Value : 0;
        try
        {
            Reserve(reserved);
            return new CsvScratchLease(
                this,
                Path.Combine(ScratchRoot, $"{Guid.NewGuid():N}.csv"),
                reserved);
        }
        catch
        {
            _concurrency.Release();
            throw;
        }
    }

    internal void EnsureReserved(CsvScratchLease lease, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (requiredBytes <= lease.ReservedBytes)
        {
            return;
        }

        var additional = requiredBytes - lease.ReservedBytes;
        Reserve(additional);
        lease.ReservedBytes = requiredBytes;
    }

    internal void Release(CsvScratchLease lease)
    {
        lock (_capacityLock)
        {
            _reservedBytes -= lease.ReservedBytes;
            _unwrittenReservedBytes -= lease.ReservedBytes - lease.WrittenBytes;
        }

        _concurrency.Release();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _concurrency.Dispose();
    }

    private void Reserve(long additionalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);

        lock (_capacityLock)
        {
            if (additionalBytes > _options.MaxAggregateScratchBytes - _reservedBytes)
            {
                throw new CsvScratchCapacityException(
                    $"This node has {_options.MaxAggregateScratchBytes} bytes of CSV scratch "
                    + "capacity and it is currently reserved by other imports. Retry later.");
            }

            EnsureFreeSpace(additionalBytes);
            _reservedBytes += additionalBytes;
            _unwrittenReservedBytes += additionalBytes;
        }
    }

    private void EnsureFreeSpace(long additionalBytes)
    {
        var root = Path.GetPathRoot(ScratchRoot);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException(
                $"Cannot resolve the filesystem containing CSV scratch root '{ScratchRoot}'.");
        }

        var free = new DriveInfo(root).AvailableFreeSpace;
        if (_unwrittenReservedBytes + additionalBytes > free - _options.MinimumFreeBytes)
        {
            throw new CsvScratchCapacityException(
                $"This node does not have enough free scratch space for the CSV upload while "
                + $"preserving the configured {_options.MinimumFreeBytes}-byte safety floor.");
        }
    }

    internal void RecordWritten(CsvScratchLease lease, int bytes)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        lock (_capacityLock)
        {
            var remaining = lease.ReservedBytes - lease.WrittenBytes;
            var materialized = Math.Min(remaining, bytes);
            lease.WrittenBytes += materialized;
            _unwrittenReservedBytes -= materialized;
        }
    }

    private void RemoveStaleFiles()
    {
        var cutoff = _clock.GetUtcNow() - _options.StaleFileAge;
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
                // Capacity checks still account for the orphan through real filesystem free space.
            }
        }
    }

    private static void ValidateOptions(CsvUploadOptions options)
    {
        if (options.MaxBytes <= 0)
        {
            throw new InvalidOperationException("Lakehold:CsvImport:MaxBytes must be positive.");
        }

        if (options.MaxAggregateScratchBytes <= 0)
        {
            throw new InvalidOperationException(
                "Lakehold:CsvImport:MaxAggregateScratchBytes must be positive.");
        }

        if (options.MaxAggregateScratchBytes < options.MaxBytes)
        {
            throw new InvalidOperationException(
                "Lakehold:CsvImport:MaxAggregateScratchBytes must be at least MaxBytes.");
        }

        if (options.MaxConcurrentUploads <= 0)
        {
            throw new InvalidOperationException(
                "Lakehold:CsvImport:MaxConcurrentUploads must be positive.");
        }

        if (options.MinimumFreeBytes < 0)
        {
            throw new InvalidOperationException(
                "Lakehold:CsvImport:MinimumFreeBytes cannot be negative.");
        }

        if (options.StaleFileAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Lakehold:CsvImport:StaleFileAge must be positive.");
        }
    }
}

/// <summary>An active node-local scratch reservation.</summary>
public sealed class CsvScratchLease : IAsyncDisposable
{
    private readonly CsvScratchSpace _owner;
    private bool _disposed;

    internal CsvScratchLease(CsvScratchSpace owner, string path, long reservedBytes)
    {
        _owner = owner;
        Path = path;
        ReservedBytes = reservedBytes;
    }

    /// <summary>Unique path owned by this upload.</summary>
    public string Path { get; }

    /// <summary>Bytes currently counted against this node's aggregate scratch budget.</summary>
    public long ReservedBytes { get; internal set; }

    internal long WrittenBytes { get; set; }

    /// <summary>Expands the aggregate reservation before more bytes are written.</summary>
    public void EnsureReserved(long requiredBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner.EnsureReserved(this, requiredBytes);
    }

    /// <summary>Marks bytes as materialized after a successful scratch-file write.</summary>
    public void RecordWritten(int bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner.RecordWritten(this, bytes);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _owner.Release(this);
        }

        return ValueTask.CompletedTask;
    }
}

using Microsoft.Extensions.Options;

namespace Lakehold.Api.Importing;

/// <summary>Raised when this node cannot safely reserve scratch capacity for another upload.</summary>
public sealed class TabularScratchCapacityException(string message) : Exception(message);

/// <summary>
///     Coordinates disposable tabular-upload scratch files across every request handled by one application
///     node.
/// </summary>
/// <remarks>
///     Scratch remains deliberately node-local because one request writes and consumes it. The
///     coordinator adds the missing node-wide invariants: bounded concurrency, bounded aggregate
///     reservations, a free-space floor, and cleanup of files abandoned by an earlier process.
/// </remarks>
public sealed class TabularScratchSpace : IDisposable
{
    private readonly CsvUploadOptions _options;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _capacityLock = new();
    private long _reservedBytes;
    private long _unwrittenReservedBytes;
    private bool _disposed;

    public TabularScratchSpace(IOptions<CsvUploadOptions> options, TimeProvider clock)
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
        if (!OperatingSystem.IsWindows())
        {
            // Uploads may contain tenant data. The directory is application-owned scratch state,
            // so no other local account needs to traverse or read it.
            File.SetUnixFileMode(
                ScratchRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        RemoveStaleFiles();

        _concurrency = new SemaphoreSlim(
            _options.MaxConcurrentUploads,
            _options.MaxConcurrentUploads);
    }

    /// <summary>The fully resolved LakeHold-owned scratch directory.</summary>
    public string ScratchRoot { get; }

    /// <summary>Reserves one upload slot and its declared size, when known.</summary>
    public async Task<TabularScratchLease> AcquireAsync(
        long? contentLength,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        var reserved = contentLength is > 0 ? contentLength.Value : 0;
        try
        {
            Reserve(reserved);
            return new TabularScratchLease(
                this,
                Path.Combine(ScratchRoot, $"{Guid.NewGuid():N}.upload"),
                reserved);
        }
        catch
        {
            _concurrency.Release();
            throw;
        }
    }

    internal void EnsureReserved(TabularScratchLease lease, long requiredBytes)
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

    internal void Release(TabularScratchLease lease)
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
                throw new TabularScratchCapacityException(
                    $"This node has {_options.MaxAggregateScratchBytes} bytes of import scratch "
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
            throw new TabularScratchCapacityException(
                $"This node does not have enough free scratch space for the upload while "
                + $"preserving the configured {_options.MinimumFreeBytes}-byte safety floor.");
        }
    }

    internal void RecordWritten(TabularScratchLease lease, int bytes)
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
        var files = Directory
            .EnumerateFiles(ScratchRoot, "*.upload", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(ScratchRoot, "*.csv", SearchOption.TopDirectoryOnly));
        foreach (var file in files)
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
public sealed class TabularScratchLease : IAsyncDisposable
{
    private readonly TabularScratchSpace _owner;
    private bool _disposed;

    internal TabularScratchLease(TabularScratchSpace owner, string path, long reservedBytes)
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

    /// <summary>Creates the upload file with owner-only permissions on Unix hosts.</summary>
    public FileStream OpenWrite()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 128 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(Path, options);
    }

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

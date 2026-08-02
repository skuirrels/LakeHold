namespace Lakehold.Api.Importing;

/// <summary>Validated node-local scratch policy shared by bounded ingestion surfaces.</summary>
internal sealed record NodeScratchOptions(
    string Root,
    string DefaultDirectory,
    string FilePattern,
    string FileExtension,
    long MaxItemBytes,
    long MaxAggregateBytes,
    int MaxConcurrentItems,
    long MinimumFreeBytes,
    TimeSpan StaleFileAge,
    string ConfigurationName);

/// <summary>
///     Coordinates bounded, disposable files for one application node. Durable workflow state must
///     live elsewhere; this type owns only capacity reservations and crash-orphan scavenging.
/// </summary>
internal sealed class NodeScratchSpace : IDisposable
{
    private readonly NodeScratchOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<string, Exception> _capacityException;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _capacityLock = new();
    private long _reservedBytes;
    private long _unwrittenReservedBytes;
    private bool _disposed;

    public NodeScratchSpace(
        NodeScratchOptions options,
        TimeProvider clock,
        Func<string, Exception> capacityException)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(capacityException);
        Validate(options);

        _options = options;
        _clock = clock;
        _capacityException = capacityException;
        ScratchRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(options.Root)
                ? Path.Combine(Path.GetTempPath(), options.DefaultDirectory)
                : options.Root);
        Directory.CreateDirectory(ScratchRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                ScratchRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        RemoveStaleFiles();
        _concurrency = new SemaphoreSlim(options.MaxConcurrentItems, options.MaxConcurrentItems);
    }

    public string ScratchRoot { get; }

    public async Task<NodeScratchLease> AcquireAsync(
        long? contentLength,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);

        var reserved = contentLength is > 0 ? contentLength.Value : 0;
        try
        {
            if (reserved > _options.MaxItemBytes)
            {
                throw _capacityException(
                    $"The requested scratch reservation exceeds the configured {_options.MaxItemBytes}-byte item limit.");
            }

            Reserve(reserved);
            return new NodeScratchLease(
                this,
                Path.Combine(ScratchRoot, $"{Guid.NewGuid():N}{_options.FileExtension}"),
                reserved);
        }
        catch
        {
            _concurrency.Release();
            throw;
        }
    }

    internal void EnsureReserved(NodeScratchLease lease, long requiredBytes)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (requiredBytes <= lease.ReservedBytes)
        {
            return;
        }

        if (requiredBytes > _options.MaxItemBytes)
        {
            throw _capacityException(
                $"The scratch file exceeds the configured {_options.MaxItemBytes}-byte item limit.");
        }

        var additional = requiredBytes - lease.ReservedBytes;
        Reserve(additional);
        lease.ReservedBytes = requiredBytes;
    }

    internal void RecordWritten(NodeScratchLease lease, int bytes)
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

    internal void Release(NodeScratchLease lease)
    {
        lock (_capacityLock)
        {
            _reservedBytes -= lease.ReservedBytes;
            _unwrittenReservedBytes -= lease.ReservedBytes - lease.WrittenBytes;
        }

        _concurrency.Release();
    }

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
            if (additionalBytes > _options.MaxAggregateBytes - _reservedBytes)
            {
                throw _capacityException(
                    $"This node has {_options.MaxAggregateBytes} bytes of scratch capacity and it is currently reserved by other operations. Retry later.");
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
                $"Cannot resolve the filesystem containing scratch root '{ScratchRoot}'.");
        }

        var free = new DriveInfo(root).AvailableFreeSpace;
        if (_unwrittenReservedBytes + additionalBytes > free - _options.MinimumFreeBytes)
        {
            throw _capacityException(
                "This node does not have enough free scratch space while preserving the configured "
                + $"{_options.MinimumFreeBytes}-byte safety floor.");
        }
    }

    private void RemoveStaleFiles()
    {
        var cutoff = _clock.GetUtcNow() - _options.StaleFileAge;
        foreach (var file in Directory.EnumerateFiles(ScratchRoot, _options.FilePattern, SearchOption.TopDirectoryOnly))
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
                // Capacity checks still account for an orphan through real filesystem free space.
            }
        }
    }

    private static void Validate(NodeScratchOptions options)
    {
        if (options.MaxItemBytes <= 0
            || options.MaxAggregateBytes < options.MaxItemBytes
            || options.MaxConcurrentItems <= 0
            || options.MinimumFreeBytes < 0
            || options.StaleFileAge <= TimeSpan.Zero
            || string.IsNullOrWhiteSpace(options.FilePattern)
            || string.IsNullOrWhiteSpace(options.FileExtension))
        {
            throw new InvalidOperationException($"{options.ConfigurationName} scratch limits are invalid.");
        }
    }
}

/// <summary>An active reservation for one owner-only node-local scratch file.</summary>
internal sealed class NodeScratchLease : IAsyncDisposable
{
    private readonly NodeScratchSpace _owner;
    private bool _disposed;

    internal NodeScratchLease(NodeScratchSpace owner, string path, long reservedBytes)
    {
        _owner = owner;
        Path = path;
        ReservedBytes = reservedBytes;
    }

    public string Path { get; }

    public long ReservedBytes { get; internal set; }

    internal long WrittenBytes { get; set; }

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

    public void EnsureReserved(long requiredBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner.EnsureReserved(this, requiredBytes);
    }

    public void RecordWritten(int bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _owner.RecordWritten(this, bytes);
    }

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

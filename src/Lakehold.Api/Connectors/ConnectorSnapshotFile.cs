using System.Text;
using System.Text.Json;
using Lakehold.Api.Importing;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>
///     Bounded node-local NDJSON staging for one refresh. It is disposable scratch state, never a
///     workflow checkpoint: the durable lease and run live in PostgreSQL and the published rows live
///     in DuckLake.
/// </summary>
internal sealed class ConnectorSnapshotFile : IAsyncDisposable
{
    private readonly NodeScratchLease _lease;
    private readonly FileStream _stream;
    private readonly ConnectorOptions _options;
    private readonly Action<string>? _cleanupFailure;
    private bool _sealed;

    private ConnectorSnapshotFile(
        NodeScratchLease lease,
        FileStream stream,
        ConnectorOptions options,
        Action<string>? cleanupFailure)
    {
        _lease = lease;
        Path = lease.Path;
        _stream = stream;
        _options = options;
        _cleanupFailure = cleanupFailure;
    }

    public string Path { get; }

    public long Rows { get; private set; }

    public long Bytes { get; private set; }

    public string? SourceVersion { get; private set; }

    public static async Task<ConnectorSnapshotFile> CreateAsync(
        ConnectorScratchSpace scratch,
        IOptions<ConnectorOptions> options,
        CancellationToken cancellationToken,
        Action<string>? cleanupFailure = null)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(options);
        var lease = await scratch.AcquireAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new ConnectorSnapshotFile(lease, lease.OpenWrite(), options.Value, cleanupFailure);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Connector records must contain a JSON object.");
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 128,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Each connector record must be one JSON object.");
        }

        var normalized = document.RootElement.GetRawText();
        var length = Encoding.UTF8.GetByteCount(normalized);
        if (length > _options.MaxRecordBytes)
        {
            throw new InvalidDataException(
                $"A connector record exceeded the {_options.MaxRecordBytes}-byte limit.");
        }

        if (Rows >= _options.MaxRows)
        {
            throw new InvalidDataException($"A connector snapshot exceeded the {_options.MaxRows}-row limit.");
        }

        if (Bytes + length + 1 > _options.MaxSnapshotBytes)
        {
            throw new InvalidDataException(
                $"A connector snapshot exceeded the {_options.MaxSnapshotBytes}-byte limit.");
        }

        var buffer = Encoding.UTF8.GetBytes(normalized + "\n");
        _lease.EnsureReserved(Bytes + buffer.Length);
        await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _lease.RecordWritten(buffer.Length);
        Rows++;
        Bytes += buffer.Length;
    }

    public void RecordSourceVersion(string? sourceVersion)
    {
        if (string.IsNullOrWhiteSpace(sourceVersion))
        {
            return;
        }

        var normalized = sourceVersion.Trim();
        if (SourceVersion is not null && !string.Equals(SourceVersion, normalized, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The connector source version changed during one snapshot.");
        }

        SourceVersion = normalized;
    }

    public async Task SealAsync(CancellationToken cancellationToken)
    {
        if (_sealed)
        {
            return;
        }

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _sealed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_sealed)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _sealed = true;
            }

            try
            {
                File.Delete(Path);
            }
            catch (FileNotFoundException)
            {
                // Cleanup is idempotent.
            }
            catch (IOException)
            {
                // Publication state must never be rewritten because disposable scratch cleanup failed.
                _cleanupFailure?.Invoke(nameof(IOException));
            }
            catch (UnauthorizedAccessException)
            {
                // Operators can remove a retained scratch file after correcting node permissions.
                _cleanupFailure?.Invoke(nameof(UnauthorizedAccessException));
            }
        }
        finally
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

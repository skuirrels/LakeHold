using System.Text;
using System.Buffers;
using System.Text.Json;
using Lakehold.Api.Importing;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>
///     Bounded node-local NDJSON staging for one refresh. It is disposable scratch state, never a
///     workflow checkpoint: the durable lease and run live in PostgreSQL and the published rows live
///     in DuckLake.
/// </summary>
internal sealed class ConnectorSnapshotFile : IDataConnectorRecordWriter, IAsyncDisposable
{
    private readonly NodeScratchLease _lease;
    private readonly FileStream _stream;
    private readonly ConnectorOptions _options;
    private readonly Action<string>? _cleanupFailure;
    private bool _sealed;
    private IReadOnlyList<DataConnectorFieldMapping> _mappings = [];

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

    public void ConfigureMappings(IReadOnlyList<DataConnectorFieldMapping> mappings)
    {
        ObjectDisposedException.ThrowIf(_sealed, this);
        ArgumentNullException.ThrowIfNull(mappings);
        if (Rows != 0)
        {
            throw new InvalidOperationException("Field mappings must be configured before records are written.");
        }

        _mappings = mappings;
    }

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

        var normalized = Project(document.RootElement);
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

    private string Project(JsonElement record)
    {
        if (_mappings.Count == 0)
        {
            return record.GetRawText();
        }

        var bySource = _mappings.ToDictionary(mapping => mapping.Source, StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (var property in record.EnumerateObject())
        {
            if (!bySource.TryGetValue(property.Name, out var mapping))
            {
                if (!targets.Add(property.Name))
                {
                    throw new InvalidDataException("Field mappings produced duplicate target columns.");
                }

                property.WriteTo(writer);
                continue;
            }

            seenSources.Add(mapping.Source);
            if (!targets.Add(mapping.Target))
            {
                throw new InvalidDataException("Field mappings produced duplicate target columns.");
            }

            writer.WritePropertyName(mapping.Target);
            WriteTransformed(writer, property.Value, mapping.Transform);
        }

        var missing = bySource.Keys.Where(source => !seenSources.Contains(source)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException("A connector record is missing a declared mapped field.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteTransformed(
        Utf8JsonWriter writer,
        JsonElement value,
        DataConnectorTransformKind transform)
    {
        if (transform == DataConnectorTransformKind.None)
        {
            value.WriteTo(writer);
            return;
        }

        var text = transform == DataConnectorTransformKind.ToString
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidDataException("String transformations require a JSON string value.");
        writer.WriteStringValue(transform switch
        {
            DataConnectorTransformKind.Trim => text?.Trim(),
            DataConnectorTransformKind.Lowercase => text?.ToLowerInvariant(),
            DataConnectorTransformKind.Uppercase => text?.ToUpperInvariant(),
            DataConnectorTransformKind.ToString => text,
            _ => throw new InvalidDataException("The connector record requested an unsupported transformation."),
        });
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

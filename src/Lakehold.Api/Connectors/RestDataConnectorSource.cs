using System.Net.Http.Headers;
using System.Text.Json;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>Reads JSON arrays or NDJSON from a bounded HTTPS GET endpoint.</summary>
internal sealed class RestDataConnectorSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ConnectorOptions> options) : IDataConnectorSource
{
    public const string HttpClientName = "lakehold-connectors";

    public DataConnectorKind Kind => DataConnectorKind.Rest;

    public async Task<ConnectorSourceResult> ReadAsync(
        DataConnector connector,
        ConnectorSnapshotFile destination,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(connector.EndpointUrl, UriKind.Absolute);
        var resolution = await OutboundDestinationPolicy.ResolveAsync(
                endpoint,
                options.Value,
                "Connector",
                cancellationToken)
            .ConfigureAwait(false);
        if (resolution.Error is not null)
        {
            throw new InvalidOperationException(resolution.Error);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (resolution.Address is not null)
        {
            request.Options.Set(OutboundConnection.ApprovedAddress, resolution.Address);
        }

        ApplyBearer(connector, request.Headers);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > options.Value.MaxSnapshotBytes)
        {
            throw new InvalidDataException(
                $"The connector response exceeds the {options.Value.MaxSnapshotBytes}-byte snapshot limit.");
        }

        var sourceVersion = response.Headers.ETag?.Tag
            ?? response.Content.Headers.LastModified?.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        destination.RecordSourceVersion(sourceVersion);

        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var limited = new LimitedReadStream(responseStream, options.Value.MaxSnapshotBytes);
        if (connector.RestResponseFormat == RestResponseFormat.JsonArray)
        {
            await ReadArrayAsync(limited, destination, timeout.Token).ConfigureAwait(false);
        }
        else
        {
            await ReadLinesAsync(limited, destination, timeout.Token).ConfigureAwait(false);
        }

        return new ConnectorSourceResult(destination.Rows, sourceVersion);
    }

    private static async Task ReadArrayAsync(
        Stream source,
        ConnectorSnapshotFile destination,
        CancellationToken cancellationToken)
    {
        await foreach (var record in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
                           source,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            await destination.WriteAsync(record.GetRawText(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReadLinesAsync(
        Stream source,
        ConnectorSnapshotFile destination,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(source, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await destination.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ApplyBearer(DataConnector connector, HttpRequestHeaders headers)
    {
        if (connector.CredentialEnvironmentVariable is not { } variable)
        {
            return;
        }

        var token = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"Connector credential environment variable '{variable}' is not available on this worker node.");
        }

        headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }
}

/// <summary>Read-only stream that fails once a source crosses its configured response ceiling.</summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private int Count(int count)
    {
        _read += count;
        if (_read > limit)
        {
            throw new InvalidDataException($"The connector response exceeded the {limit}-byte limit.");
        }

        return count;
    }
}

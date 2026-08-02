using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

/// <summary>Reads JSON arrays or NDJSON from a bounded HTTPS GET endpoint.</summary>
internal sealed class RestDataConnectorSource(
    IHttpClientFactory httpClientFactory,
    IOptions<ConnectorOptions> options,
    ConnectorSecretResolver secrets) : IDataConnectorSource
{
    public const string HttpClientName = "lakehold-connectors";

    public ConnectorAdapterManifest Manifest { get; } = new(
        "lakehold.rest",
        1,
        DataConnectorKind.Rest,
        new HashSet<DataConnectorReadMode> { DataConnectorReadMode.FullSnapshot },
        new HashSet<DataConnectorAuthenticationKind>
        {
            DataConnectorAuthenticationKind.None,
            DataConnectorAuthenticationKind.Bearer,
            DataConnectorAuthenticationKind.MutualTls,
            DataConnectorAuthenticationKind.CustomHeader,
        },
        SupportsSourceVersion: true);

    public async Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken)
    {
        var connector = context.Connector;
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

        var authentication = connector.Authentication();
        await ApplyAuthenticationAsync(context, authentication, endpoint.DnsSafeHost, request, cancellationToken)
            .ConfigureAwait(false);
        using var ownedClient = await CreateMtlsClientAsync(
                context,
                authentication,
                endpoint.DnsSafeHost,
                resolution.Address,
                cancellationToken)
            .ConfigureAwait(false);
        var client = ownedClient ?? httpClientFactory.CreateClient(HttpClientName);
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

        return new ConnectorSourceResult(sourceVersion);
    }

    private static async Task ReadArrayAsync(
        Stream source,
        IDataConnectorRecordWriter destination,
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
        IDataConnectorRecordWriter destination,
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

    private async Task ApplyAuthenticationAsync(
        ConnectorReadContext context,
        DataConnectorAuthentication authentication,
        string destinationHost,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        switch (authentication.Kind)
        {
            case DataConnectorAuthenticationKind.None:
            case DataConnectorAuthenticationKind.MutualTls:
                return;
            case DataConnectorAuthenticationKind.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    await secrets.ResolveAsync(
                            authentication.SecretReference
                            ?? throw new InvalidOperationException("Bearer authentication requires a secret reference."),
                            context.TenantSlug,
                            context.CatalogName,
                            destinationHost,
                            cancellationToken)
                        .ConfigureAwait(false));
                return;
            case DataConnectorAuthenticationKind.CustomHeader:
                var header = authentication.CustomHeaderName;
                if (string.IsNullOrWhiteSpace(header)
                    || !header.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                {
                    throw new InvalidOperationException("Custom authentication requires a valid HTTP header name.");
                }

                request.Headers.TryAddWithoutValidation(
                    header,
                    await secrets.ResolveAsync(
                            authentication.SecretReference
                            ?? throw new InvalidOperationException("Custom authentication requires a secret reference."),
                            context.TenantSlug,
                            context.CatalogName,
                            destinationHost,
                            cancellationToken)
                        .ConfigureAwait(false));
                return;
            default:
                throw new InvalidOperationException("The REST adapter does not support this authentication mechanism.");
        }
    }

    private async Task<HttpClient?> CreateMtlsClientAsync(
        ConnectorReadContext context,
        DataConnectorAuthentication authentication,
        string destinationHost,
        System.Net.IPAddress? approvedAddress,
        CancellationToken cancellationToken)
    {
        if (authentication.Kind != DataConnectorAuthenticationKind.MutualTls)
        {
            return null;
        }

        var certificateValue = await secrets.ResolveAsync(
                authentication.ClientCertificateSecretReference
                ?? throw new InvalidOperationException("mTLS authentication requires a certificate secret reference."),
                context.TenantSlug,
                context.CatalogName,
                destinationHost,
                cancellationToken)
            .ConfigureAwait(false);
        string? password = null;
        if (authentication.CertificatePasswordSecretReference is { } passwordReference)
        {
            password = await secrets.ResolveAsync(
                    passwordReference,
                    context.TenantSlug,
                    context.CatalogName,
                    destinationHost,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var certificateBytes = Convert.FromBase64String(certificateValue);
        var certificate = X509CertificateLoader.LoadPkcs12(certificateBytes, password);
        var handler = OutboundConnection.CreateHandler(approvedAddress);
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
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

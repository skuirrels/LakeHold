using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Lakehold.Api.Connectors.Grpc;
using Lakehold.Api.Security;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Connectors;

internal sealed record GrpcConnectorRecord(string Json, string? SourceVersion);

/// <summary>Transport seam for the generated LakeHold protobuf client.</summary>
internal interface IGrpcConnectorTransport
{
    IAsyncEnumerable<GrpcConnectorRecord> ReadAsync(
        ConnectorReadContext context,
        CancellationToken cancellationToken);
}

/// <summary>Projects a server-streamed gRPC snapshot into the common bounded JSON staging format.</summary>
internal sealed class GrpcDataConnectorSource(IGrpcConnectorTransport transport) : IDataConnectorSource
{
    public ConnectorAdapterManifest Manifest { get; } = new(
        "lakehold.grpc",
        1,
        DataConnectorKind.Grpc,
        new HashSet<DataConnectorReadMode> { DataConnectorReadMode.FullSnapshot },
        new HashSet<DataConnectorAuthenticationKind>
        {
            DataConnectorAuthenticationKind.None,
            DataConnectorAuthenticationKind.Bearer,
        },
        SupportsSourceVersion: true);

    public async Task<ConnectorSourceResult> ReadAsync(
        ConnectorReadContext context,
        IDataConnectorRecordWriter destination,
        CancellationToken cancellationToken)
    {
        string? sourceVersion = null;
        await foreach (var record in transport.ReadAsync(context, cancellationToken).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(record.SourceVersion))
            {
                if (sourceVersion is not null
                    && !string.Equals(sourceVersion, record.SourceVersion, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The gRPC source changed its source_version during one snapshot stream.");
                }

                sourceVersion = record.SourceVersion;
                destination.RecordSourceVersion(record.SourceVersion);
            }

            await destination.WriteAsync(record.Json, cancellationToken).ConfigureAwait(false);
        }

        return new ConnectorSourceResult(sourceVersion);
    }
}

/// <summary>Network implementation of LakeHold's small server-streaming protobuf contract.</summary>
internal sealed class GrpcConnectorTransport(
    IOptions<ConnectorOptions> options,
    ConnectorSecretResolver secrets) : IGrpcConnectorTransport
{
    public async IAsyncEnumerable<GrpcConnectorRecord> ReadAsync(
        ConnectorReadContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
        using var handler = OutboundConnection.CreateHandler(resolution.Address);
        var maxMessageBytes = checked(options.Value.MaxRecordBytes + 64 * 1024);
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler,
            MaxReceiveMessageSize = maxMessageBytes,
        });
        var client = new DataSource.DataSourceClient(channel);
        using var call = client.Read(
            new ReadRequest
            {
                ConnectorName = connector.Name,
                Tenant = connector.Tenant.Slug,
                Catalog = connector.Catalog.Name,
            },
            await BearerHeadersAsync(context, endpoint.DnsSafeHost, cancellationToken).ConfigureAwait(false),
            cancellationToken: timeout.Token);

        await foreach (var record in call.ResponseStream.ReadAllAsync(timeout.Token).ConfigureAwait(false))
        {
            yield return new GrpcConnectorRecord(record.Json, record.SourceVersion);
        }
    }

    private async Task<Metadata?> BearerHeadersAsync(
        ConnectorReadContext context,
        string destinationHost,
        CancellationToken cancellationToken)
    {
        var connector = context.Connector;
        var authentication = connector.Authentication();
        if (authentication.Kind == DataConnectorAuthenticationKind.None)
        {
            return null;
        }

        if (authentication.Kind != DataConnectorAuthenticationKind.Bearer)
        {
            throw new InvalidOperationException("The gRPC adapter supports only bearer authentication.");
        }

        var token = await secrets.ResolveAsync(
                authentication.SecretReference
                ?? throw new InvalidOperationException("Bearer authentication requires a secret reference."),
                context.TenantSlug,
                context.CatalogName,
                destinationHost,
                cancellationToken)
            .ConfigureAwait(false);
        return new Metadata { { "authorization", $"Bearer {token}" } };
    }
}

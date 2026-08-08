using System.Net;
using System.Net.Sockets;
using Lakehold.Api.Connectors;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
/// Real Kafka and Schema Registry protocol evidence, over the mandatory SOCKS/HTTP gateways.
/// </summary>
/// <remarks>
/// This runs only inside the Linux Compose fixture, which <c>scripts/test-kafka-avro-proxy.sh</c>
/// starts before setting <c>LAKEHOLD_KAFKA_FIXTURE_RUNNING</c>. There is deliberately no host-side
/// fallback: the registry leg is TLS against the fixture CA, and that CA is only in the container's
/// trust store. A fallback that skipped the TLS leg would report the same green tick for strictly
/// less evidence.
/// </remarks>
public sealed class KafkaAvroProxyFixtureTests
{
    [SkippableFact]
    public async Task Kafka_avro_source_reads_registry_backed_records_only_through_the_required_gateways()
    {
        Skip.If(
            !string.Equals(Environment.GetEnvironmentVariable("LAKEHOLD_KAFKA_FIXTURE_RUNNING"), "1", StringComparison.Ordinal),
            "Kafka Avro protocol evidence runs in the Linux Compose fixture; use scripts/test-kafka-avro-proxy.sh.");

        await ReadExistingFixtureAsync("172.31.240.13", 1080, "172.31.240.14", 8888, "/fixture-certs/ca.pem");
    }

    private static async Task ReadExistingFixtureAsync(string socksHost, int socksPort, string proxyHost, int proxyPort, string ca)
    {
        Assert.True(File.Exists(ca), "The Linux fixture CA is not installed.");
        await using var kafkaGateway = await SocksTcpGateway.StartAsync(socksHost, socksPort, new IPEndPoint(IPAddress.Parse("172.31.240.10"), 19092));
        var username = "LAKEHOLD_FIXTURE_SR_USER";
        var password = "LAKEHOLD_FIXTURE_SR_PASSWORD";
        Environment.SetEnvironmentVariable(username, "lakehold-fixture"); Environment.SetEnvironmentVariable(password, "lakehold-fixture-password");
        try
        {
            var options = Options.Create(new ConnectorOptions { AllowUnsafeDestinations = true, RequestTimeout = TimeSpan.FromSeconds(15), KafkaEgressGateway = new KafkaEgressGatewayOptions { PolicyName = "fixture", KafkaBootstrapGateway = "127.0.0.1:19092", SchemaRegistryHttpProxy = $"http://{proxyHost}:{proxyPort}", SchemaRegistryCaCertificatePath = ca }, SecretBindings = [new() { TenantSlug = "fixture", CatalogName = "fixture", Reference = $"env://{username}", DestinationHost = "172.31.240.12" }, new() { TenantSlug = "fixture", CatalogName = "fixture", Reference = $"env://{password}", DestinationHost = "172.31.240.12" }] });
            var connector = DataConnector.Create(1, 1, new DataConnectorDefinition("avro", null, "fixture@example.test", [], DataConnectorKind.KafkaAvro, "https://172.31.240.12:8443", null, RestResponseFormat.JsonArray, "main", "lifecycle", 1, ["id"], ["id"], false, null, new DataConnectorPlatformDefinition("lakehold.kafka-avro", 1, DataConnectorReadMode.Incremental, DataConnectorSchemaPolicy.Reject, ["id"], [], new DataConnectorSourceSettings(PageSize: 1, KafkaBootstrapServers: "172.31.240.10:9092", KafkaTopic: "lakehold-avro-proxy-test", KafkaConsumerGroup: "lakehold-fixture-" + Guid.NewGuid().ToString("N"), SchemaRegistryUrl: "https://172.31.240.12:8443"), new DataConnectorAuthentication(DataConnectorAuthenticationKind.None, SchemaRegistryUsernameSecretReference: $"env://{username}", SchemaRegistryPasswordSecretReference: $"env://{password}"))), DateTimeOffset.UtcNow);
            var source = new KafkaAvroDataConnectorSource(new ConnectorSecretResolver([new EnvironmentConnectorSecretProvider()], options), options); var writer = new CapturingWriter();
            await source.ReadAsync(new ConnectorReadContext(connector, null, "fixture", "fixture"), writer, default);
            Assert.True(writer.Rows == 1, $"Expected one decoded record, found {writer.Rows}.");
            Assert.Contains("\"state\":\"accepted\"", writer.RowsJson.Single(), StringComparison.Ordinal);
            await ((IConnectorPostPublicationAcknowledger)source).AcknowledgePublishedAsync(default);
        }
        finally { Environment.SetEnvironmentVariable(username, null); Environment.SetEnvironmentVariable(password, null); }
    }

    private sealed class CapturingWriter : IDataConnectorRecordWriter
    {
        public List<string> RowsJson { get; } = [];
        public long Rows => RowsJson.Count;
        public Task WriteAsync(string json, CancellationToken cancellationToken) { RowsJson.Add(json); return Task.CompletedTask; }
        public void RecordSourceVersion(string? sourceVersion) { }
    }

    /// <summary>Test stand-in for a deployment-owned literal-IP Kafka egress gateway.</summary>
    private sealed class SocksTcpGateway(TcpListener listener, int socksPort, IPEndPoint destination) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private Task _acceptLoop = Task.CompletedTask;
        public int Connections { get; private set; }
        public int SocksConnections { get; private set; }

        public static async Task<SocksTcpGateway> StartAsync(string socksHost, int socksPort, IPEndPoint destination)
        {
            var listener = new TcpListener(IPAddress.Loopback, 19092);
            listener.Start();
            var gateway = new SocksTcpGateway(listener, socksPort, destination) { SocksHost = socksHost };
            gateway._acceptLoop = gateway.AcceptAsync();
            await Task.Yield();
            return gateway;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(_stop.Token);
                    Connections++;
                    _ = RelayAsync(client);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task RelayAsync(TcpClient client)
        {
            using var ownedClient = client;
            using var socks = new TcpClient();
            await socks.ConnectAsync(SocksHost, socksPort, _stop.Token);
            var stream = socks.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, _stop.Token);
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, _stop.Token);
            if (greeting is not [5, 0]) throw new InvalidOperationException("Fixture SOCKS gateway rejected no-authentication.");
            var address = destination.Address.GetAddressBytes();
            var request = new byte[6 + address.Length]; request[0] = 5; request[1] = 1; request[2] = 0; request[3] = address.Length == 4 ? (byte)1 : (byte)4;
            address.CopyTo(request, 4); request[^2] = (byte)(destination.Port >> 8); request[^1] = (byte)destination.Port;
            await stream.WriteAsync(request, _stop.Token);
            var reply = new byte[4]; await stream.ReadExactlyAsync(reply, _stop.Token);
            if (reply[1] != 0) throw new InvalidOperationException("Fixture SOCKS gateway could not connect to Kafka.");
            SocksConnections++;
            var remaining = reply[3] switch { 1 => 6, 4 => 18, 3 => await ReadDomainLengthAsync(stream), _ => throw new InvalidOperationException("Invalid SOCKS reply.") };
            await stream.ReadExactlyAsync(new byte[remaining], _stop.Token);
            await Task.WhenAny(client.GetStream().CopyToAsync(stream, _stop.Token), stream.CopyToAsync(client.GetStream(), _stop.Token));
        }

        private string SocksHost { get; init; } = "127.0.0.1";

        private static async Task<int> ReadDomainLengthAsync(NetworkStream stream) { var length = new byte[1]; await stream.ReadExactlyAsync(length); return length[0] + 2; }
        public async ValueTask DisposeAsync() { _stop.Cancel(); listener.Stop(); try { await _acceptLoop; } catch (OperationCanceledException) { } _stop.Dispose(); }
    }
}

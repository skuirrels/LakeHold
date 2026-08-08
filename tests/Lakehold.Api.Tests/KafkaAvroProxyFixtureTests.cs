using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lakehold.Api.Connectors;
using Lakehold.ControlPlane.Model;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Real Docker protocol evidence; skips only when the Docker daemon is unavailable.</summary>
public sealed class KafkaAvroProxyFixtureTests
{
    [SkippableFact]
    public async Task Kafka_avro_source_reads_registry_backed_records_only_through_the_required_gateways()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("LAKEHOLD_KAFKA_FIXTURE_RUNNING"), "1", StringComparison.Ordinal))
        {
            await ReadExistingFixtureAsync("172.31.240.13", 1080, "172.31.240.14", 8888, "/fixture-certs/ca.pem");
            return;
        }
        Skip.If(!await DockerAvailableAsync(), "Docker is unavailable; Kafka fixture cannot run.");
        var root = Root();
        var compose = Path.Combine(root, "tests/fixtures/kafka-avro-proxy/compose.yaml");
        var project = "lakehold-kafka-avro-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker(root, "compose", "-p", project, "-f", compose, "up", "-d", "--wait", "kafka", "schema-registry", "schema-registry-tls", "socks-gateway", "registry-proxy");
            await Docker(root, "compose", "-p", project, "-f", compose, "exec", "-T", "kafka", "kafka-topics", "--bootstrap-server", "kafka:9092", "--create", "--if-not-exists", "--topic", "lakehold-avro-proxy-test", "--partitions", "1", "--replication-factor", "1");
            var producerName = project + "-producer";
            await Docker(root, "compose", "-p", project, "-f", compose, "run", "--name", producerName, "avro-producer");
            var producerOutput = await Docker(root, "logs", producerName);
            Assert.Contains("Fixture Avro lifecycle record published", producerOutput, StringComparison.Ordinal);
            var produced = await Docker(root, "compose", "-p", project, "-f", compose, "exec", "-T", "schema-registry", "kafka-avro-console-consumer", "--bootstrap-server", "kafka:9092", "--topic", "lakehold-avro-proxy-test", "--from-beginning", "--max-messages", "1", "--timeout-ms", "15000", "--property", "schema.registry.url=http://schema-registry:8081", "--property", "basic.auth.credentials.source=USER_INFO", "--property", "basic.auth.user.info=lakehold-fixture:lakehold-fixture-password");
            Assert.True(produced.Contains("accepted", StringComparison.OrdinalIgnoreCase),
                $"The fixture producer did not publish the expected lifecycle record. Producer output: {producerOutput}");
            var socksPort = await PublishedPort(root, project, compose, "socks-gateway", "1080");
            var registryProxyPort = await PublishedPort(root, project, compose, "registry-proxy", "8888");
            var ca = Path.Combine(root, "tests/fixtures/kafka-avro-proxy/certs/ca.pem");
            Assert.True(File.Exists(ca), "The TLS fixture CA was not created.");


            await using var kafkaGateway = await SocksTcpGateway.StartAsync(socksPort, new IPEndPoint(IPAddress.Parse("172.31.240.10"), 19092));
            var username = "LAKEHOLD_FIXTURE_SR_USER_" + Guid.NewGuid().ToString("N");
            var password = "LAKEHOLD_FIXTURE_SR_PASSWORD_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(username, "lakehold-fixture");
            Environment.SetEnvironmentVariable(password, "lakehold-fixture-password");
            try
            {
                var options = Options.Create(new ConnectorOptions
                {
                    AllowUnsafeDestinations = true,
                    RequestTimeout = TimeSpan.FromSeconds(15),
                    KafkaEgressGateway = new KafkaEgressGatewayOptions
                    {
                        PolicyName = "fixture-only",
                        KafkaBootstrapGateway = "127.0.0.1:19092",
                        SchemaRegistryHttpProxy = $"http://127.0.0.1:{registryProxyPort}",
                        SchemaRegistryCaCertificatePath = ca,
                    },
                    SecretBindings =
                    [
                        new() { TenantSlug = "fixture", CatalogName = "fixture", Reference = $"env://{username}", DestinationHost = "172.31.240.12" },
                        new() { TenantSlug = "fixture", CatalogName = "fixture", Reference = $"env://{password}", DestinationHost = "172.31.240.12" },
                    ],
                });
                var definition = new DataConnectorDefinition(
                    "avro-fixture", null, "fixture@example.test", [], DataConnectorKind.KafkaAvro,
                    "https://172.31.240.12:8443", null, RestResponseFormat.JsonArray, "main", "lifecycle", 1,
                    ["id"], ["id"], false, null,
                    new DataConnectorPlatformDefinition(
                        "lakehold.kafka-avro", 1, DataConnectorReadMode.Incremental, DataConnectorSchemaPolicy.Reject,
                        ["id"], [],
                        new DataConnectorSourceSettings(PageSize: 10, KafkaBootstrapServers: "172.31.240.10:9092", KafkaTopic: "lakehold-avro-proxy-test", KafkaConsumerGroup: "lakehold-fixture-" + Guid.NewGuid().ToString("N"), SchemaRegistryUrl: "https://172.31.240.12:8443"),
                        new DataConnectorAuthentication(DataConnectorAuthenticationKind.None, SchemaRegistryUsernameSecretReference: $"env://{username}", SchemaRegistryPasswordSecretReference: $"env://{password}")));
                var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);
                var source = new KafkaAvroDataConnectorSource(new ConnectorSecretResolver([new EnvironmentConnectorSecretProvider()], options), options);
                var writer = new CapturingWriter();
                var result = await source.ReadAsync(new ConnectorReadContext(connector, null, "fixture", "fixture"), writer, default);
                Assert.True(writer.Rows == 1,
                    $"Expected one decoded record, found {writer.Rows}. Kafka TCP gateway accepted {kafkaGateway.Connections} connection(s) and completed {kafkaGateway.SocksConnections} SOCKS connection(s).");
                Assert.Contains("\"state\":\"accepted\"", writer.RowsJson.Single(), StringComparison.Ordinal);
                Assert.NotNull(result.ProposedCheckpoint);
                await ((IConnectorPostPublicationAcknowledger)source).AcknowledgePublishedAsync(default);
            }
            finally
            {
                Environment.SetEnvironmentVariable(username, null);
                Environment.SetEnvironmentVariable(password, null);
            }

            var socksLog = await Docker(root, "compose", "-p", project, "-f", compose, "logs", "socks-gateway");
            var proxyLog = await Docker(root, "compose", "-p", project, "-f", compose, "logs", "registry-proxy");
            Assert.Contains("socks", socksLog, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CONNECT", proxyLog, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Docker(root,
                ["compose", "-p", project, "-f", compose, "down", "-v", "--remove-orphans"],
                allowFailure: true);
        }
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

    private static async Task<int> PublishedPort(string root, string project, string compose, string service, string privatePort)
    {
        var output = await Docker(root, "compose", "-p", project, "-f", compose, "port", service, privatePort);
        var value = output.Trim().Split(':').Last();
        return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
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

        public static async Task<SocksTcpGateway> StartAsync(int socksPort, IPEndPoint destination) => await StartAsync("127.0.0.1", socksPort, destination);
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

    private static async Task<bool> DockerAvailableAsync()
    {
        try { await Docker(Root(), "version", "--format", "{{.Server.Version}}"); return true; }
        catch { return false; }
    }
    private static string Root()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
            if (File.Exists(Path.Combine(path.FullName, "Lakehold.slnx"))) return path.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }
    private static async Task<string> Docker(string directory, params string[] arguments) => await Docker(directory, arguments, false);
    private static async Task<string> Docker(string directory, string[] arguments, bool allowFailure)
    {
        using var process = Process.Start(new ProcessStartInfo("docker") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, Arguments = string.Join(' ', arguments.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)) })!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        if (!allowFailure && process.ExitCode != 0) throw new Xunit.Sdk.XunitException(output);
        return output;
    }
}

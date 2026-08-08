using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Connectors;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class DataConnectorTests
{
    [Fact]
    public async Task Durable_claim_allows_one_worker_and_records_lineage()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-connector-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
            builder.UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}");
            await using var context = new ControlPlaneContext(builder.Options);
            await context.Database.EnsureCreatedAsync();
            var tenant = new Tenant
            {
                Slug = "acme",
                DisplayName = "Acme",
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            var catalog = new LakeCatalog
            {
                Tenant = tenant,
                Name = "analytics",
                MetadataSource = Path.Combine(root, "analytics.ducklake"),
                DataPath = Path.Combine(root, "data"),
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            context.Catalogs.Add(catalog);
            await context.SaveChangesAsync();

            var service = new DataConnectorService(context);
            var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
            var connector = await service.CreateAsync(
                "acme",
                "analytics",
                Definition(DataConnectorKind.Rest),
                now,
                default);

            var first = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-one",
                now,
                TimeSpan.FromMinutes(5),
                default);
            var second = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-two",
                now,
                TimeSpan.FromMinutes(5),
                default);

            Assert.NotNull(first);
            Assert.Null(second);
            var updateWhileRunning = await Assert.ThrowsAsync<DataConnectorConflictException>(() =>
                service.UpdateAsync(
                    "acme",
                    "analytics",
                    connector.Id,
                    first!.Connector.ConcurrencyVersion,
                    Definition(DataConnectorKind.Rest) with { Description = "changed while running" },
                    now.AddSeconds(1),
                    default));
            Assert.Contains("currently refreshing", updateWhileRunning.Message, StringComparison.Ordinal);
            var running = Assert.Single(
                await service.ListRunsAsync("acme", "analytics", connector.Id, 10, default));
            Assert.Equal(first!.LeaseToken, running.LeaseToken);

            var replacement = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-two",
                now.AddMinutes(6),
                TimeSpan.FromMinutes(5),
                default);
            Assert.NotNull(replacement);
            Assert.Null(await service.TryBeginPublicationAsync(first, now.AddMinutes(6), default));

            await using (var publication = await service.TryBeginPublicationAsync(
                             replacement!,
                             now.AddMinutes(6),
                             default))
            {
                Assert.NotNull(publication);
                await publication.CompleteAsync(now.AddMinutes(6).AddSeconds(3), 2, 2, "v1", default);
            }
            var runs = await service.ListRunsAsync("acme", "analytics", connector.Id, 10, default);
            Assert.Equal(2, runs.Count);
            var run = Assert.Single(runs, item => item.Status == DataConnectorRunStatus.Succeeded);
            Assert.Single(runs, item => item.Status == DataConnectorRunStatus.Failed);
            Assert.Equal(DataConnectorRunStatus.Succeeded, run.Status);
            Assert.True(run.QualityPassed);
            Assert.Equal(2, run.RowsPublished);
            Assert.Equal("v1", run.SourceVersion);

            var publishedConnector = await service.GetAsync("acme", "analytics", connector.Id, default);
            Assert.True(publishedConnector.TargetProvisioned);
            var retarget = Assert.Throws<InvalidOperationException>(() => publishedConnector.Reconfigure(
                Definition(DataConnectorKind.Rest) with { TargetTable = "other_orders" },
                now.AddMinutes(6).AddSeconds(4)));
            Assert.Contains("cannot change", retarget.Message, StringComparison.Ordinal);

            var staleRetire = await Assert.ThrowsAsync<DataConnectorConflictException>(() =>
                service.DeleteAsync("acme", "analytics", connector.Id, publishedConnector.ConcurrencyVersion - 1, default));
            Assert.Contains("reload", staleRetire.Message, StringComparison.OrdinalIgnoreCase);

            // Clients generated before optimistic delete versions were introduced omit the query
            // parameter. Preserve that additive contract while newer clients can still opt into
            // the explicit stale-write protection proven above.
            await service.DeleteAsync("acme", "analytics", connector.Id, null, default);
            var archived = await service.GetAsync("acme", "analytics", connector.Id, default);
            Assert.NotNull(archived.ArchivedUtc);
            Assert.True(archived.TargetProvisioned);
            Assert.False(archived.Enabled);
            Assert.Equal(2, (await service.ListRunsAsync(
                "acme",
                "analytics",
                connector.Id,
                10,
                default)).Count);
            Assert.Null(await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-three",
                now.AddMinutes(7),
                TimeSpan.FromMinutes(5),
                default));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Rest_array_is_normalised_to_bounded_ndjson()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""[{"id":1,"name":"one"},{"id":2,"name":"two"}]"""),
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        var options = TestOptions();
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        var source = new RestDataConnectorSource(
            new StubHttpClientFactory(new HttpClient(new StubHandler(response))),
            options,
            SecretResolver(options));
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);

        var connector = DataConnector.Create(1, 1, Definition(DataConnectorKind.Rest), DateTimeOffset.UtcNow);
        var result = await source.ReadAsync(
            new ConnectorReadContext(connector, connector.Checkpoint, "test-tenant", "test-catalog"),
            snapshot,
            default);
        await snapshot.SealAsync(default);

        Assert.Equal(2, snapshot.Rows);
        Assert.Equal("\"v1\"", result.SourceVersion);
        Assert.Equal(
            ["""{"id":1,"name":"one"}""", """{"id":2,"name":"two"}"""],
            await File.ReadAllLinesAsync(snapshot.Path));
    }

    [Fact]
    public async Task Grpc_stream_requires_one_consistent_source_version()
    {
        var options = TestOptions();
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        var source = new GrpcDataConnectorSource(new StubGrpcTransport(
            new GrpcConnectorRecord("""{"id":1}""", "snapshot-7"),
            new GrpcConnectorRecord("""{"id":2}""", "snapshot-7")));
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);

        var connector = DataConnector.Create(1, 1, Definition(DataConnectorKind.Grpc), DateTimeOffset.UtcNow);
        var result = await source.ReadAsync(
            new ConnectorReadContext(connector, connector.Checkpoint, "test-tenant", "test-catalog"),
            snapshot,
            default);

        Assert.Equal(2, snapshot.Rows);
        Assert.Equal("snapshot-7", result.SourceVersion);
    }

    [Fact]
    public async Task Snapshot_rejects_non_object_records()
    {
        var options = TestOptions();
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => snapshot.WriteAsync("[1,2,3]", default));
        Assert.Contains("JSON object", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Completed_run_cannot_be_rewritten()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var run = DataConnectorRun.Start(1, DataConnectorTrigger.Manual, "node-one", "lease-one", now);
        run.Succeed(now.AddSeconds(1), 2, 2, "v1");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            run.Fail(now.AddSeconds(2), 2, "v1", true, "late cleanup failure"));

        Assert.Contains("running", failure.Message, StringComparison.Ordinal);
        Assert.Equal(DataConnectorRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.RowsPublished);
    }

    [Fact]
    public void Published_run_records_source_acknowledgement_pending_without_claiming_success()
    {
        var now = DateTimeOffset.UtcNow;
        var run = DataConnectorRun.Start(1, DataConnectorTrigger.Manual, "node-one", "lease-one", now);
        run.Succeed(now.AddSeconds(1), 4, 4, "topic:0:3", "topic:0:4", "topic:0:4");

        run.MarkSourceAcknowledgementPending(
            now.AddSeconds(2),
            "LakeHold published the batch, but source acknowledgement is pending; a later run will replay it safely.");

        Assert.Equal(DataConnectorRunStatus.PublishedAwaitingSourceAcknowledgement, run.Status);
        Assert.Equal(4, run.RowsPublished);
        Assert.Contains("published", run.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<InvalidOperationException>(() => run.MarkSourceAcknowledgementPending(now.AddSeconds(3), "again"));
    }

    [Fact]
    public void Connector_requires_explicit_retry_after_source_acknowledgement_failure()
    {
        var now = DateTimeOffset.UtcNow;
        var connector = DataConnector.Create(
            1,
            1,
            Definition(DataConnectorKind.KafkaAvro) with
            {
                EndpointUrl = "https://registry.example.test",
                Enabled = true,
                RefreshIntervalSeconds = 60,
                Platform = new DataConnectorPlatformDefinition(
                    "lakehold.kafka-avro", 1, DataConnectorReadMode.Incremental,
                    DataConnectorSchemaPolicy.Reject, ["id"], [],
                    new DataConnectorSourceSettings(KafkaBootstrapServers: "198.51.100.10:9092", KafkaTopic: "orders", KafkaConsumerGroup: "lakehold", SchemaRegistryUrl: "https://registry.example.test"),
                    new DataConnectorAuthentication())
            },
            now);

        connector.MarkSourceAcknowledgementPending(now.AddSeconds(1), "published but Kafka did not commit");

        Assert.NotNull(connector.SourceAcknowledgementPendingUtc);
        Assert.Null(connector.NextRunUtc);
        Assert.Contains("Kafka", connector.SourceAcknowledgementError, StringComparison.OrdinalIgnoreCase);

        connector.Resume(now.AddSeconds(2), resetFailures: true);

        Assert.Null(connector.SourceAcknowledgementPendingUtc);
        Assert.Null(connector.SourceAcknowledgementError);
        Assert.Equal(now.AddSeconds(2), connector.NextRunUtc);
    }

    [Fact]
    public async Task Persisted_acknowledgement_pending_gate_survives_reload_and_explicit_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-connector-ack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<ControlPlaneContext>().UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}").Options;
            await using (var setup = new ControlPlaneContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                var tenant = new Tenant { Slug = "ack", DisplayName = "Ack", CreatedUtc = DateTimeOffset.UtcNow };
                var catalog = new LakeCatalog { Tenant = tenant, Name = "main", MetadataSource = Path.Combine(root, "main.ducklake"), DataPath = Path.Combine(root, "data"), CreatedUtc = DateTimeOffset.UtcNow };
                setup.AddRange(tenant, catalog); await setup.SaveChangesAsync();
                var connector = DataConnector.Create(tenant.Id, catalog.Id, Definition(DataConnectorKind.Rest), DateTimeOffset.UtcNow);
                setup.Add(connector); await setup.SaveChangesAsync();
                var run = DataConnectorRun.Start(connector.Id, DataConnectorTrigger.Manual, "node", "lease", DateTimeOffset.UtcNow);
                run.Succeed(DateTimeOffset.UtcNow, 1, 1, "topic:0:0", "topic:0:1", "topic:0:1");
                setup.Add(run); await setup.SaveChangesAsync();
                var service = new DataConnectorService(setup);
                await service.MarkSourceAcknowledgementPendingAsync(run.Id, DateTimeOffset.UtcNow, "published but source acknowledgement failed", default);
            }
            await using (var verify = new ControlPlaneContext(options))
            {
                var connector = await verify.DataConnectors.SingleAsync(); var run = await verify.DataConnectorRuns.SingleAsync();
                Assert.NotNull(connector.SourceAcknowledgementPendingUtc); Assert.Equal(DataConnectorRunStatus.PublishedAwaitingSourceAcknowledgement, run.Status);
                connector.Resume(DateTimeOffset.UtcNow, true); await verify.SaveChangesAsync();
            }
            await using var replay = new ControlPlaneContext(options);
            Assert.Null((await replay.DataConnectors.SingleAsync()).SourceAcknowledgementPendingUtc);
        }
        finally { Directory.Delete(root, true); }
    }

    private static DataConnectorDefinition Definition(DataConnectorKind kind) => new(
        "orders",
        "Order snapshot",
        "data-platform@example.test",
        ["orders", "managed"],
        kind,
        "http://example.test/orders",
        CredentialEnvironmentVariable: null,
        RestResponseFormat.JsonArray,
        "main",
        "orders",
        MinimumRows: 1,
        RequiredColumns: ["id"],
        NotNullColumns: ["id"],
        Enabled: false,
        RefreshIntervalSeconds: null);

    private static IOptions<ConnectorOptions> TestOptions() => Options.Create(new ConnectorOptions
    {
        AllowHttp = true,
        AllowUnsafeDestinations = true,
        MaxSnapshotBytes = 1024 * 1024,
        MaxRecordBytes = 1024,
        MaxRows = 100,
        RequestTimeout = TimeSpan.FromSeconds(10),
    });

    private static ConnectorSecretResolver SecretResolver(IOptions<ConnectorOptions> options) => new(
        [new EnvironmentConnectorSecretProvider()],
        options);

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class StubGrpcTransport(params GrpcConnectorRecord[] records) : IGrpcConnectorTransport
    {
        public async IAsyncEnumerable<GrpcConnectorRecord> ReadAsync(
            ConnectorReadContext context,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return record;
            }
        }
    }
}

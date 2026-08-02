using System.Net;
using Grpc.Core;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Connectors;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using TestGrpc = Lakehold.Api.Tests.GrpcServer;

namespace Lakehold.Api.Tests;

public sealed class DataConnectorTransportIntegrationTests
{
    [Fact]
    public async Task Rest_ndjson_sends_bearer_and_reads_real_http_response()
    {
        var credentialVariable = $"LAKEHOLD_CONNECTOR_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(credentialVariable, "transport-secret");
        try
        {
            string? authorization = null;
            await using var server = await LoopbackServer.StartAsync(
                HttpProtocols.Http1,
                app => app.MapGet("/records", (HttpContext context) =>
                {
                    authorization = context.Request.Headers.Authorization;
                    context.Response.ContentType = "application/x-ndjson";
                    context.Response.Headers.ETag = "\"snapshot-9\"";
                    return context.Response.WriteAsync("{\"id\":1}\n{\"id\":2}\n");
                }));
            var options = TestOptions();
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new RestDataConnectorSource(
                new HttpClientFactory(new HttpClient()),
                options);
            var definition = Definition(DataConnectorKind.Rest, server.Url + "/records") with
            {
                CredentialEnvironmentVariable = credentialVariable,
                RestResponseFormat = RestResponseFormat.NewlineDelimitedJson,
            };

            var result = await source.ReadAsync(DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow), snapshot, default);

            Assert.Equal("Bearer transport-secret", authorization);
            Assert.Equal(2, result.RowsRead);
            Assert.Equal("\"snapshot-9\"", result.SourceVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    public async Task Rest_timeout_is_enforced_by_connector_policy()
    {
        await using var server = await LoopbackServer.StartAsync(
            HttpProtocols.Http1,
            app => app.MapGet("/slow", async context =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
                await context.Response.WriteAsync("[]", context.RequestAborted);
            }));
        var options = TestOptions(requestTimeout: TimeSpan.FromMilliseconds(50));
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
        var source = new RestDataConnectorSource(new HttpClientFactory(new HttpClient()), options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(
            DataConnector.Create(
                1,
                1,
                Definition(DataConnectorKind.Rest, server.Url + "/slow"),
                DateTimeOffset.UtcNow),
            snapshot,
            default));
    }

    [Fact]
    public async Task Rest_content_length_above_snapshot_limit_is_refused()
    {
        await using var server = await LoopbackServer.StartAsync(
            HttpProtocols.Http1,
            app => app.MapGet("/large", () => Results.Text("[{\"payload\":\"too-large\"}]", "application/json")));
        var options = TestOptions(maxSnapshotBytes: 8);
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
        var source = new RestDataConnectorSource(new HttpClientFactory(new HttpClient()), options);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadAsync(
            DataConnector.Create(
                1,
                1,
                Definition(DataConnectorKind.Rest, server.Url + "/large"),
                DateTimeOffset.UtcNow),
            snapshot,
            default));

        Assert.Contains("snapshot limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grpc_contract_streams_records_and_bearer_metadata_over_real_http2()
    {
        var credentialVariable = $"LAKEHOLD_CONNECTOR_TEST_{Guid.NewGuid():N}";
        string? controlRoot = null;
        Environment.SetEnvironmentVariable(credentialVariable, "grpc-secret");
        try
        {
            var capture = new GrpcCapture();
            await using var server = await LoopbackServer.StartAsync(
                HttpProtocols.Http2,
                app => app.MapGrpcService<TestDataSource>(),
                services =>
                {
                    services.AddGrpc();
                    services.AddSingleton(capture);
                });
            var options = TestOptions();
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new GrpcDataConnectorSource(new GrpcConnectorTransport(options));
            var definition = Definition(DataConnectorKind.Grpc, server.Url) with
            {
                CredentialEnvironmentVariable = credentialVariable,
            };
            controlRoot = Path.Combine(Path.GetTempPath(), "lakehold-grpc-control", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(controlRoot);
            var contextOptions = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(controlRoot, "control.duckdb")}")
                .Options;
            await using var context = new ControlPlaneContext(contextOptions);
            await context.Database.EnsureCreatedAsync();
            context.Catalogs.Add(new LakeCatalog
            {
                Tenant = new Tenant
                {
                    Slug = "acme",
                    DisplayName = "Acme",
                    CreatedUtc = DateTimeOffset.UtcNow,
                },
                Name = "analytics",
                MetadataSource = Path.Combine(controlRoot, "analytics.ducklake"),
                DataPath = Path.Combine(controlRoot, "data"),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            var service = new DataConnectorService(context);
            var connector = await service.CreateAsync("acme", "analytics", definition, DateTimeOffset.UtcNow, default);
            var claim = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "grpc-test",
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1),
                default);

            var result = await source.ReadAsync(claim!.Connector, snapshot, default);

            Assert.Equal("Bearer grpc-secret", capture.Authorization);
            Assert.Equal("orders", capture.Request?.ConnectorName);
            Assert.Equal("acme", capture.Request?.Tenant);
            Assert.Equal("analytics", capture.Request?.Catalog);
            Assert.Equal(2, result.RowsRead);
            Assert.Equal("grpc-v4", result.SourceVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
            if (controlRoot is not null)
            {
                try
                {
                    Directory.Delete(controlRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Test scratch cleanup must not hide a transport assertion.
                }
            }
        }
    }

    private static IOptions<ConnectorOptions> TestOptions(
        TimeSpan? requestTimeout = null,
        long maxSnapshotBytes = 1024 * 1024) => Options.Create(new ConnectorOptions
        {
            AllowHttp = true,
            AllowUnsafeDestinations = true,
            MaxSnapshotBytes = maxSnapshotBytes,
            MaxAggregateScratchBytes = maxSnapshotBytes,
            MinimumFreeBytes = 0,
            MaxRecordBytes = (int)Math.Min(1024, maxSnapshotBytes),
            MaxRows = 100,
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10),
            ScratchRoot = Path.Combine(Path.GetTempPath(), "lakehold-connector-transport-tests"),
        });

    private static DataConnectorDefinition Definition(DataConnectorKind kind, string endpoint) => new(
        "orders",
        null,
        "data-platform@example.test",
        [],
        kind,
        endpoint,
        null,
        RestResponseFormat.JsonArray,
        "main",
        "orders",
        1,
        ["id"],
        ["id"],
        false,
        null);

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    public sealed class GrpcCapture
    {
        public string? Authorization { get; set; }
        public TestGrpc.ReadRequest? Request { get; set; }
    }

    public sealed class TestDataSource(GrpcCapture capture) : TestGrpc.DataSource.DataSourceBase
    {
        public override async Task Read(
            TestGrpc.ReadRequest request,
            IServerStreamWriter<TestGrpc.DataRecord> responseStream,
            ServerCallContext context)
        {
            capture.Authorization = context.RequestHeaders.GetValue("authorization");
            capture.Request = request;
            await responseStream.WriteAsync(
                new TestGrpc.DataRecord { Json = "{\"id\":1}", SourceVersion = "grpc-v4" });
            await responseStream.WriteAsync(
                new TestGrpc.DataRecord { Json = "{\"id\":2}", SourceVersion = "grpc-v4" });
        }
    }

    private sealed class LoopbackServer(WebApplication app, string url) : IAsyncDisposable
    {
        public string Url { get; } = url;

        public static async Task<LoopbackServer> StartAsync(
            HttpProtocols protocols,
            Action<WebApplication> map,
            Action<IServiceCollection>? configureServices = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(kestrel =>
                kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = protocols));
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            map(app);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = Assert.Single(addresses ?? []);
            return new LoopbackServer(app, address.TrimEnd('/'));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

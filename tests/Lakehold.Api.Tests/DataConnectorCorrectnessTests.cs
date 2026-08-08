using System.Text.Json;
using Lakehold.Api.Auth;
using Lakehold.Api.Connectors;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class DataConnectorCorrectnessTests : IDisposable
{
    private readonly string _scratchRoot = Path.Combine(
        Path.GetTempPath(),
        "lakehold-connector-scratch-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Kafka_bootstrap_addresses_are_validated_but_egress_gateway_is_mandatory()
    {
        Assert.Equal(["broker-one.example", "broker-two.example"],
            DataConnectorEndpoints.GetKafkaBootstrapHosts("broker-one.example:9092,broker-two.example:9093"));
        Assert.Throws<ArgumentException>(() => DataConnectorEndpoints.GetKafkaBootstrapHosts("https://broker.example:9092"));
        Assert.Throws<ArgumentException>(() => DataConnectorEndpoints.GetKafkaBootstrapHosts("user:password@broker.example:9092"));
        Assert.Throws<ArgumentException>(() => DataConnectorEndpoints.GetKafkaBootstrapHosts("broker.example:9092/path"));
    }

    [Fact]
    public void Kafka_egress_gateway_rejects_direct_or_dns_addressed_configuration()
    {
        var direct = new ConnectorOptions();
        Assert.False(direct.TryGetKafkaEgressGateway(out _, out var directError));
        Assert.Contains("disabled", directError, StringComparison.OrdinalIgnoreCase);

        var rebindingGateway = new ConnectorOptions
        {
            KafkaEgressGateway = new KafkaEgressGatewayOptions
            {
                PolicyName = "approved-kafka",
                KafkaBootstrapGateway = "proxy.example.test:1080",
                SchemaRegistryHttpProxy = "http://198.51.100.9:8080",
            },
        };
        Assert.False(rebindingGateway.TryGetKafkaEgressGateway(out _, out _));

        var supported = new ConnectorOptions
        {
            KafkaEgressGateway = new KafkaEgressGatewayOptions
            {
                PolicyName = "approved-kafka",
                KafkaBootstrapGateway = "198.51.100.10:1080",
                SchemaRegistryHttpProxy = "http://198.51.100.11:8080",
            },
        };
        Assert.True(supported.TryGetKafkaEgressGateway(out var gateway, out var error));
        Assert.Null(error);
        Assert.Equal("approved-kafka", gateway.PolicyName);
    }

    [Fact]
    public void Failed_run_preserves_partial_evidence_without_claiming_quality_was_evaluated()
    {
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var run = DataConnectorRun.Start(1, DataConnectorTrigger.Manual, "node-one", "lease-one", now);

        run.Fail(now.AddSeconds(2), 17, "source-v3", qualityPassed: null, "source disconnected");

        Assert.Equal(DataConnectorRunStatus.Failed, run.Status);
        Assert.Equal(17, run.RowsRead);
        Assert.Equal(0, run.RowsPublished);
        Assert.Null(run.QualityPassed);
        Assert.Equal("source-v3", run.SourceVersion);
    }

    [Fact]
    public void Definitions_reject_inert_schedules_empty_quality_and_unknown_kinds()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => DataConnector.Create(
            1,
            1,
            Definition() with { Enabled = true, RefreshIntervalSeconds = null },
            now));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataConnector.Create(
            1,
            1,
            Definition() with { MinimumRows = 0 },
            now));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataConnector.Create(
            1,
            1,
            Definition() with { Kind = (DataConnectorKind)999 },
            now));
    }

    [Fact]
    public void Schedule_change_is_due_at_reconfiguration_time()
    {
        var created = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var changed = created.AddMinutes(10);
        var connector = DataConnector.Create(
            1,
            1,
            Definition() with { Enabled = true, RefreshIntervalSeconds = 60 },
            created);

        connector.Reconfigure(
            Definition() with { Enabled = true, RefreshIntervalSeconds = 300 },
            changed);

        Assert.Equal(changed, connector.NextRunUtc);
    }

    [Theory]
    [InlineData((int)ConnectorExecutionFailureKind.None, StatusCodes.Status200OK)]
    [InlineData((int)ConnectorExecutionFailureKind.ClaimConflict, StatusCodes.Status409Conflict)]
    [InlineData((int)ConnectorExecutionFailureKind.Quality, StatusCodes.Status422UnprocessableEntity)]
    [InlineData((int)ConnectorExecutionFailureKind.TargetConflict, StatusCodes.Status409Conflict)]
    [InlineData((int)ConnectorExecutionFailureKind.Capacity, StatusCodes.Status503ServiceUnavailable)]
    [InlineData((int)ConnectorExecutionFailureKind.SourceOrImport, StatusCodes.Status502BadGateway)]
    [InlineData((int)ConnectorExecutionFailureKind.PublicationState, StatusCodes.Status500InternalServerError)]
    public async Task Manual_execution_result_uses_truthful_http_status(
        int failureKindValue,
        int expectedStatus)
    {
        var failureKind = (ConnectorExecutionFailureKind)failureKindValue;
        var result = DataConnectorEndpoints.ToHttpResult(new ConnectorExecutionResult(
            7,
            failureKind == ConnectorExecutionFailureKind.None ? "succeeded" : "failed",
            3,
            failureKind == ConnectorExecutionFailureKind.None ? 3 : 0,
            "v1",
            failureKind == ConnectorExecutionFailureKind.None ? null : "safe error",
            failureKind));
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal(7, json.RootElement.GetProperty("runId").GetInt32());
    }

    [Fact]
    public async Task Every_connector_endpoint_requires_tenant_owner_capability()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddRouting();
        builder.Services.AddScoped<DataConnectorService>();
        builder.Services.AddScoped<ConnectorRunner>();
        builder.Services.AddScoped<DataConnectorSourceResolver>();
        builder.Services.AddOptions<ConnectorOptions>();
        await using var app = builder.Build();

        app.MapGroup("/api/tenants").MapDataConnectorEndpoints();

        var connectorEndpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.DisplayName?.Contains("connectors", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        Assert.Equal(11, connectorEndpoints.Length);
        Assert.All(connectorEndpoints, endpoint => Assert.Equal(
            Capability.TenantOwner,
            endpoint.Metadata.GetMetadata<RouteCapabilityMetadata>()?.Capability));
    }

    [Fact]
    public async Task Connector_scratch_enforces_aggregate_capacity_and_owner_only_files()
    {
        var options = ScratchOptions(maxAggregateBytes: 8);
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        await using var first = await scratch.AcquireAsync(default);
        first.EnsureReserved(6);

        await using var second = await scratch.AcquireAsync(default);
        var error = Assert.Throws<ConnectorScratchCapacityException>(() => second.EnsureReserved(3));
        Assert.Contains("currently reserved", error.Message, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(scratch.ScratchRoot));
            await using var stream = second.OpenWrite();
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(second.Path));
        }
    }

    [Fact]
    public void Connector_scratch_removes_stale_crash_orphans()
    {
        Directory.CreateDirectory(_scratchRoot);
        var stale = Path.Combine(_scratchRoot, "stale.ndjson");
        var recent = Path.Combine(_scratchRoot, "recent.ndjson");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        using var scratch = new ConnectorScratchSpace(ScratchOptions(8), TimeProvider.System);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratchRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // Temp cleanup must not hide the assertion that failed.
        }
    }

    private IOptions<ConnectorOptions> ScratchOptions(long maxAggregateBytes) => Options.Create(new ConnectorOptions
    {
        ScratchRoot = _scratchRoot,
        MaxSnapshotBytes = 8,
        MaxAggregateScratchBytes = maxAggregateBytes,
        MaxConcurrentRuns = 2,
        MinimumFreeBytes = 0,
        StaleFileAge = TimeSpan.FromDays(1),
        MaxRecordBytes = 8,
    });

    private static DataConnectorDefinition Definition() => new(
        "orders",
        null,
        "data-platform@example.test",
        [],
        DataConnectorKind.Rest,
        "https://example.test/orders",
        null,
        RestResponseFormat.JsonArray,
        "main",
        "orders",
        1,
        ["id"],
        ["id"],
        false,
        null);
}

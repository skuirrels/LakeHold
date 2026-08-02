using DuckDB.EFCoreProvider.Extensions;
using System.Net;
using Lakehold.Api.Connectors;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class ConnectorPlatformTests
{
    [Fact]
    public async Task Field_mappings_apply_only_bounded_declarative_transforms()
    {
        var options = Options.Create(new ConnectorOptions
        {
            ScratchRoot = Path.Combine(Path.GetTempPath(), "lakehold-mapping-tests", Guid.NewGuid().ToString("N")),
            MaxSnapshotBytes = 1024,
            MaxAggregateScratchBytes = 1024,
            MinimumFreeBytes = 0,
            MaxRecordBytes = 1024,
            MaxRows = 10,
        });
        using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
        await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
        snapshot.ConfigureMappings(
        [
            new DataConnectorFieldMapping("displayName", "customer_name", DataConnectorTransformKind.Trim),
            new DataConnectorFieldMapping("region", "region", DataConnectorTransformKind.Uppercase),
        ]);

        await snapshot.WriteAsync(
            "{\"id\":7,\"displayName\":\"  Ada  \",\"region\":\"eu\"}",
            default);
        await snapshot.SealAsync(default);

        Assert.Equal(
            "{\"id\":7,\"customer_name\":\"Ada\",\"region\":\"EU\"}",
            Assert.Single(await File.ReadAllLinesAsync(snapshot.Path)));
    }

    [Fact]
    public void Field_mappings_require_mapped_version_and_are_canonicalized()
    {
        var invalid = IncrementalDefinition() with
        {
            Platform = IncrementalDefinition().Platform! with
            {
                FieldMappings = [new DataConnectorFieldMapping("name", "customer_name")],
            },
        };
        Assert.Throws<ArgumentException>(() =>
            DataConnector.Create(1, 1, invalid, DateTimeOffset.UtcNow));

        var mapped = invalid with
        {
            Platform = invalid.Platform! with
            {
                SchemaPolicy = DataConnectorSchemaPolicy.MappedVersion,
                FieldMappings = [new DataConnectorFieldMapping(" name ", " customer_name ")],
            },
        };
        var connector = DataConnector.Create(1, 1, mapped, DateTimeOffset.UtcNow);

        Assert.Equal("name", Assert.Single(connector.FieldMappings()).Source);
        Assert.Equal("customer_name", Assert.Single(connector.FieldMappings()).Target);
    }

    [Fact]
    public void PostgreSql_cursor_requires_a_commit_monotonic_contract_and_unambiguous_type()
    {
        var valid = IncrementalDefinition();
        var withoutCommitContract = valid with
        {
            Platform = valid.Platform! with
            {
                SourceSettings = valid.Platform.SourceSettings with { CursorIsCommitMonotonic = false },
            },
        };
        Assert.Throws<ArgumentException>(() =>
            DataConnector.Create(1, 1, withoutCommitContract, DateTimeOffset.UtcNow));

        var ambiguousTimestamp = valid with
        {
            Platform = valid.Platform! with
            {
                SourceSettings = valid.Platform.SourceSettings with { CursorType = "timestamp" },
            },
        };
        Assert.Throws<ArgumentException>(() =>
            DataConnector.Create(1, 1, ambiguousTimestamp, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Checkpoint_commits_after_publication_and_failures_back_off_then_dead_letter()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-platform-control", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var contextOptions = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}")
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
                MetadataSource = Path.Combine(root, "analytics.ducklake"),
                DataPath = Path.Combine(root, "data"),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
            var service = new DataConnectorService(context);
            var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
            var connector = await service.CreateAsync("acme", "analytics", IncrementalDefinition(), now, default);

            var first = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-one",
                now,
                TimeSpan.FromMinutes(1),
                default);
            await service.CompleteFailureAsync(
                first!,
                now.AddSeconds(1),
                0,
                null,
                null,
                new string('x', 5_000),
                default);
            var afterFirst = await service.GetAsync("acme", "analytics", connector.Id, default);
            Assert.Null(afterFirst.Checkpoint);
            Assert.Equal(1, afterFirst.ConsecutiveFailures);
            Assert.Equal(now.AddSeconds(11), afterFirst.NextRunUtc);
            Assert.Null(afterFirst.PausedUtc);
            var firstFailure = Assert.Single(
                await service.ListRunsAsync("acme", "analytics", connector.Id, 10, default),
                run => run.Status == DataConnectorRunStatus.Failed);
            Assert.Equal(4_000, firstFailure.Error!.Length);

            var second = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-two",
                now.AddSeconds(2),
                TimeSpan.FromMinutes(1),
                default);
            await service.CompleteFailureAsync(
                second!,
                now.AddSeconds(3),
                0,
                null,
                null,
                "second source failure",
                default);
            var deadLettered = await service.GetAsync("acme", "analytics", connector.Id, default);
            Assert.Equal(2, deadLettered.ConsecutiveFailures);
            Assert.NotNull(deadLettered.PausedUtc);
            Assert.Null(await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "blocked-node",
                now.AddSeconds(4),
                TimeSpan.FromMinutes(1),
                default));
            Assert.Contains(
                await service.ListRunsAsync("acme", "analytics", connector.Id, 10, default),
                run => run.Status == DataConnectorRunStatus.DeadLettered);

            await service.ResumeAsync(
                "acme",
                "analytics",
                connector.Id,
                deadLettered.ConcurrencyVersion,
                resetFailures: true,
                now.AddSeconds(5),
                default);
            var third = await service.TryClaimAsync(
                connector.Id,
                DataConnectorTrigger.Manual,
                "node-three",
                now.AddSeconds(6),
                TimeSpan.FromMinutes(1),
                default);
            await using (var fence = await service.TryBeginPublicationAsync(third!, now.AddSeconds(7), default))
            {
                Assert.NotNull(fence);
                await fence.CompleteAsync(
                    now.AddSeconds(8),
                    5,
                    5,
                    "42",
                    proposedCheckpoint: "42",
                    replayKey: "<initial>->42",
                    targetPublished: true,
                    default);
            }

            var completed = await service.GetAsync("acme", "analytics", connector.Id, default);
            Assert.Equal("42", completed.Checkpoint);
            Assert.Equal(1, completed.CheckpointVersion);
            Assert.Equal(0, completed.ConsecutiveFailures);
            Assert.Null(completed.PausedUtc);
            var succeeded = Assert.Single(
                await service.ListRunsAsync("acme", "analytics", connector.Id, 10, default),
                run => run.Status == DataConnectorRunStatus.Succeeded);
            Assert.Null(succeeded.InputCheckpoint);
            Assert.Equal("42", succeeded.ProposedCheckpoint);
            Assert.Equal("<initial>->42", succeeded.ReplayKey);
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
    public async Task Environment_secret_provider_returns_values_without_persisting_them()
    {
        var variable = $"LAKEHOLD_SECRET_TEST_{Guid.NewGuid():N}";
        var reference = $"env://{variable}";
        Environment.SetEnvironmentVariable(variable, "test-secret-value");
        try
        {
            var options = Options.Create(new ConnectorOptions
            {
                SecretBindings =
                [
                    new ConnectorSecretBindingOptions
                    {
                        TenantSlug = "test-tenant",
                        CatalogName = "test-catalog",
                        Reference = reference,
                        DestinationHost = "example.test",
                    },
                ],
            });
            var resolver = new ConnectorSecretResolver([new EnvironmentConnectorSecretProvider()], options);
            Assert.Equal(
                "test-secret-value",
                await resolver.ResolveAsync(
                    reference,
                    "test-tenant",
                    "test-catalog",
                    "example.test",
                    default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(
                    reference,
                    "other-tenant",
                    "test-catalog",
                    "example.test",
                    default));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                resolver.ResolveAsync(
                    reference,
                    "test-tenant",
                    "test-catalog",
                    "other.example.test",
                    default));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Operator_adapter_manifest_is_selectable_without_a_built_in_id()
    {
        var source = new TestConnectorSource(new ConnectorAdapterManifest(
            "operator.rest",
            7,
            DataConnectorKind.Rest,
            new HashSet<DataConnectorReadMode> { DataConnectorReadMode.FullSnapshot },
            new HashSet<DataConnectorAuthenticationKind> { DataConnectorAuthenticationKind.None },
            SupportsSourceVersion: true));
        var resolver = new DataConnectorSourceResolver([source]);
        var definition = new DataConnectorDefinition(
            "operator-source",
            null,
            "data-platform@example.test",
            [],
            DataConnectorKind.Rest,
            "https://source.example.test/records",
            null,
            RestResponseFormat.JsonArray,
            "main",
            "operator_records",
            1,
            [],
            [],
            Enabled: false,
            RefreshIntervalSeconds: null,
            new DataConnectorPlatformDefinition(
                source.Manifest.Id,
                source.Manifest.Version,
                DataConnectorReadMode.FullSnapshot,
                DataConnectorSchemaPolicy.Reject,
                [],
                [],
                new DataConnectorSourceSettings(),
                new DataConnectorAuthentication()));
        var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);

        Assert.Same(source.Manifest, resolver.FindManifest("operator.rest", 7));
        Assert.Same(source, resolver.Resolve(connector));

        var request = new DataConnectorDefinitionRequest(
            "operator-source",
            null,
            "data-platform@example.test",
            [],
            "rest",
            "https://source.example.test/records",
            null,
            "json-array",
            "main",
            "operator_records",
            AdapterId: "operator.rest",
            AdapterVersion: 7,
            Authentication: new DataConnectorAuthenticationRequest("none"));
        var platform = DataConnectorEndpoints.BuildPlatformDefinition(
            request,
            DataConnectorKind.Rest,
            resolver);
        Assert.Null(platform.Error);
        Assert.Equal("operator.rest", platform.Definition!.AdapterId);
        Assert.Equal(7, platform.Definition.AdapterVersion);
    }

    [Fact]
    public void Malformed_nested_connector_fields_return_validation_errors()
    {
        var source = new TestConnectorSource(new ConnectorAdapterManifest(
            "operator.rest",
            1,
            DataConnectorKind.Rest,
            new HashSet<DataConnectorReadMode> { DataConnectorReadMode.FullSnapshot },
            new HashSet<DataConnectorAuthenticationKind> { DataConnectorAuthenticationKind.None },
            SupportsSourceVersion: false));
        var resolver = new DataConnectorSourceResolver([source]);
        var valid = new DataConnectorDefinitionRequest(
            "operator-source",
            null,
            "data-platform@example.test",
            [],
            "rest",
            "https://source.example.test/records",
            null,
            "json-array",
            "main",
            "operator_records",
            AdapterId: "operator.rest",
            Authentication: new DataConnectorAuthenticationRequest("none"));

        var nullAuthenticationKind = DataConnectorEndpoints.BuildPlatformDefinition(
            valid with { Authentication = new DataConnectorAuthenticationRequest(null!) },
            DataConnectorKind.Rest,
            resolver);
        Assert.Contains("kind is required", nullAuthenticationKind.Error, StringComparison.Ordinal);

        var nullMapping = DataConnectorEndpoints.BuildPlatformDefinition(
            valid with
            {
                SchemaPolicy = "mapped-version",
                FieldMappings = [null!],
            },
            DataConnectorKind.Rest,
            resolver);
        Assert.Contains("Every field mapping", nullMapping.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vault_secret_provider_uses_bounded_authenticated_https_contract()
    {
        var tokenVariable = $"LAKEHOLD_VAULT_TOKEN_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(tokenVariable, "vault-access-token");
        try
        {
            var handler = new VaultHandler();
            var provider = new VaultConnectorSecretProvider(
                new HttpClientFactory(new HttpClient(handler)),
                Options.Create(new ConnectorOptions
                {
                    AllowUnsafeDestinations = true,
                    SecretProviderEndpoint = "https://vault.example.test/",
                    SecretProviderTokenEnvironmentVariable = tokenVariable,
                    RequestTimeout = TimeSpan.FromSeconds(5),
                }));

            Assert.Equal("resolved-secret", await provider.ResolveAsync("erp-password", default));
            Assert.Equal("Bearer vault-access-token", handler.Authorization);
            Assert.Equal("/secrets/erp-password", handler.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenVariable, null);
        }
    }

    private static DataConnectorDefinition IncrementalDefinition() => new(
        "orders",
        null,
        "data-platform@example.test",
        [],
        DataConnectorKind.PostgreSql,
        "postgresql://db.example.test/lakehold",
        null,
        RestResponseFormat.JsonArray,
        "main",
        "orders",
        1,
        ["id"],
        ["id"],
        Enabled: true,
        RefreshIntervalSeconds: 60,
        new DataConnectorPlatformDefinition(
            "lakehold.postgresql",
            1,
            DataConnectorReadMode.Incremental,
            DataConnectorSchemaPolicy.Reject,
            ["id"],
            [],
            new DataConnectorSourceSettings(
                "public.orders",
                "id",
                "int64",
                100,
                CursorIsCommitMonotonic: true),
            new DataConnectorAuthentication(
                DataConnectorAuthenticationKind.PostgreSqlPassword,
                UsernameSecretReference: "env://PGUSER",
                PasswordSecretReference: "env://PGPASSWORD"),
            MaxAttempts: 2,
            RetryBaseSeconds: 10,
            RetryMaxSeconds: 60));

    private sealed class TestConnectorSource(ConnectorAdapterManifest manifest) : IDataConnectorSource
    {
        public ConnectorAdapterManifest Manifest { get; } = manifest;

        public Task<ConnectorSourceResult> ReadAsync(
            ConnectorReadContext context,
            IDataConnectorRecordWriter destination,
            CancellationToken cancellationToken) => Task.FromResult(new ConnectorSourceResult("test-version"));
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class VaultHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"value\":\"resolved-secret\"}"),
            });
        }
    }
}

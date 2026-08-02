using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
using Npgsql;
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
            Bind(options, $"env://{credentialVariable}", new Uri(server.Url).DnsSafeHost);
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new RestDataConnectorSource(
                new HttpClientFactory(new HttpClient()),
                options,
                SecretResolver(options));
            var definition = Definition(DataConnectorKind.Rest, server.Url + "/records") with
            {
                CredentialEnvironmentVariable = credentialVariable,
                RestResponseFormat = RestResponseFormat.NewlineDelimitedJson,
            };

            var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);
            var result = await source.ReadAsync(
                Context(connector, connector.Checkpoint),
                snapshot,
                default);

            Assert.Equal("Bearer transport-secret", authorization);
            Assert.Equal(2, snapshot.Rows);
            Assert.Equal("\"snapshot-9\"", result.SourceVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable(credentialVariable, null);
        }
    }

    [Fact]
    public async Task Rest_custom_api_key_is_resolved_without_entering_the_definition()
    {
        var secretVariable = $"LAKEHOLD_CONNECTOR_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretVariable, "resolved-api-key");
        try
        {
            string? apiKey = null;
            await using var server = await LoopbackServer.StartAsync(
                HttpProtocols.Http1,
                app => app.MapGet("/records", (HttpContext context) =>
                {
                    apiKey = context.Request.Headers["X-Api-Key"];
                    return Results.Json(new[] { new { id = 1 } });
                }));
            var options = TestOptions();
            Bind(options, $"env://{secretVariable}", new Uri(server.Url).DnsSafeHost);
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new RestDataConnectorSource(
                new HttpClientFactory(new HttpClient()),
                options,
                SecretResolver(options));
            var definition = Definition(DataConnectorKind.Rest, server.Url + "/records") with
            {
                Platform = new DataConnectorPlatformDefinition(
                    "lakehold.rest",
                    1,
                    DataConnectorReadMode.FullSnapshot,
                    DataConnectorSchemaPolicy.Reject,
                    [],
                    [],
                    new DataConnectorSourceSettings(),
                    new DataConnectorAuthentication(
                        DataConnectorAuthenticationKind.CustomHeader,
                        SecretReference: $"env://{secretVariable}",
                        CustomHeaderName: "X-Api-Key")),
            };
            var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);

            var result = await source.ReadAsync(
                Context(connector, connector.Checkpoint),
                snapshot,
                default);

            Assert.Equal(1, snapshot.Rows);
            Assert.Equal("resolved-api-key", apiKey);
            Assert.DoesNotContain("resolved-api-key", connector.AuthenticationJson, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVariable, null);
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
        var source = new RestDataConnectorSource(
            new HttpClientFactory(new HttpClient()),
            options,
            SecretResolver(options));

        var connector = DataConnector.Create(
                1,
                1,
                Definition(DataConnectorKind.Rest, server.Url + "/slow"),
                DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(
            Context(connector, connector.Checkpoint),
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
        var source = new RestDataConnectorSource(
            new HttpClientFactory(new HttpClient()),
            options,
            SecretResolver(options));

        var connector = DataConnector.Create(
                1,
                1,
                Definition(DataConnectorKind.Rest, server.Url + "/large"),
                DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadAsync(
            Context(connector, connector.Checkpoint),
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
            Bind(options, $"env://{credentialVariable}", new Uri(server.Url).DnsSafeHost, "acme", "analytics");
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new GrpcDataConnectorSource(new GrpcConnectorTransport(options, SecretResolver(options)));
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

            var result = await source.ReadAsync(
                new ConnectorReadContext(claim!.Connector, claim.Connector.Checkpoint, "acme", "analytics"),
                snapshot,
                default);

            Assert.Equal("Bearer grpc-secret", capture.Authorization);
            Assert.Equal("orders", capture.Request?.ConnectorName);
            Assert.Equal("acme", capture.Request?.Tenant);
            Assert.Equal("analytics", capture.Request?.Catalog);
            Assert.Equal(2, snapshot.Rows);
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

    [Fact]
    public async Task HubSpot_adapter_renews_oauth_and_returns_incremental_checkpoint()
    {
        var clientId = $"LAKEHOLD_HUBSPOT_CLIENT_{Guid.NewGuid():N}";
        var clientSecret = $"LAKEHOLD_HUBSPOT_SECRET_{Guid.NewGuid():N}";
        var refreshToken = $"LAKEHOLD_HUBSPOT_REFRESH_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(clientId, "client-id");
        Environment.SetEnvironmentVariable(clientSecret, "client-secret");
        Environment.SetEnvironmentVariable(refreshToken, "refresh-token");
        try
        {
            var handler = new HubSpotHandler();
            var options = TestOptions();
            Bind(options, $"env://{clientId}", "api.hubapi.com");
            Bind(options, $"env://{clientSecret}", "api.hubapi.com");
            Bind(options, $"env://{refreshToken}", "api.hubapi.com");
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new HubSpotContactsDataConnectorSource(
                new HttpClientFactory(new HttpClient(handler)),
                options,
                SecretResolver(options),
                TimeProvider.System,
                new HubSpotRequestLimiter(options, TimeProvider.System));
            var definition = Definition(DataConnectorKind.HubSpot, "https://api.hubapi.com") with
            {
                Platform = new DataConnectorPlatformDefinition(
                    "lakehold.hubspot-contacts",
                    1,
                    DataConnectorReadMode.Incremental,
                    DataConnectorSchemaPolicy.Reject,
                    ["id"],
                    [],
                    new DataConnectorSourceSettings(PageSize: 100, Properties: ["email"]),
                    new DataConnectorAuthentication(
                        DataConnectorAuthenticationKind.OAuthRefreshToken,
                        ClientIdSecretReference: $"env://{clientId}",
                        ClientSecretReference: $"env://{clientSecret}",
                        RefreshTokenSecretReference: $"env://{refreshToken}")),
            };
            var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);

            var result = await source.ReadAsync(
                Context(connector, "2026-08-01T00:00:00.0000000+00:00"),
                snapshot,
                default);

            Assert.Equal(2, snapshot.Rows);
            Assert.True(DateTimeOffset.Parse(result.ProposedCheckpoint!) > new DateTimeOffset(2026, 8, 2, 10, 30, 0, TimeSpan.Zero));
            Assert.Equal(3, handler.SearchRequests);
            Assert.Equal("Bearer renewed-access-token", handler.SearchAuthorization);
            Assert.Contains("refresh_token", handler.TokenBody, StringComparison.Ordinal);
            Assert.Contains("lastmodifieddate", handler.SearchBody, StringComparison.Ordinal);

            var limitedOptions = TestOptions();
            limitedOptions.Value.MaxPaginationPages = 1;
            Bind(limitedOptions, $"env://{clientId}", "api.hubapi.com");
            Bind(limitedOptions, $"env://{clientSecret}", "api.hubapi.com");
            Bind(limitedOptions, $"env://{refreshToken}", "api.hubapi.com");
            using var limitedScratch = new ConnectorScratchSpace(limitedOptions, TimeProvider.System);
            await using var limitedSnapshot = await ConnectorSnapshotFile.CreateAsync(
                limitedScratch,
                limitedOptions,
                default);
            var limitedSource = new HubSpotContactsDataConnectorSource(
                new HttpClientFactory(new HttpClient(new HubSpotHandler())),
                limitedOptions,
                SecretResolver(limitedOptions),
                TimeProvider.System,
                new HubSpotRequestLimiter(limitedOptions, TimeProvider.System));
            var limitFailure = await Assert.ThrowsAsync<InvalidDataException>(() => limitedSource.ReadAsync(
                Context(connector, "2026-08-01T00:00:00.0000000+00:00"),
                limitedSnapshot,
                default));
            Assert.Contains("1-page limit", limitFailure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(clientId, null);
            Environment.SetEnvironmentVariable(clientSecret, null);
            Environment.SetEnvironmentVariable(refreshToken, null);
        }
    }

    [Fact]
    public async Task HubSpot_adapter_narrows_a_search_window_before_the_provider_ceiling()
    {
        var clientId = $"LAKEHOLD_HUBSPOT_WINDOW_CLIENT_{Guid.NewGuid():N}";
        var clientSecret = $"LAKEHOLD_HUBSPOT_WINDOW_SECRET_{Guid.NewGuid():N}";
        var refreshToken = $"LAKEHOLD_HUBSPOT_WINDOW_REFRESH_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(clientId, "client-id");
        Environment.SetEnvironmentVariable(clientSecret, "client-secret");
        Environment.SetEnvironmentVariable(refreshToken, "refresh-token");
        try
        {
            var threshold = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var handler = new HubSpotWindowHandler(threshold);
            var options = TestOptions();
            Bind(options, $"env://{clientId}", "api.hubapi.com");
            Bind(options, $"env://{clientSecret}", "api.hubapi.com");
            Bind(options, $"env://{refreshToken}", "api.hubapi.com");
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
            using var scratch = new ConnectorScratchSpace(options, clock);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new HubSpotContactsDataConnectorSource(
                new HttpClientFactory(new HttpClient(handler)),
                options,
                SecretResolver(options),
                clock,
                new HubSpotRequestLimiter(options, clock));
            var definition = Definition(DataConnectorKind.HubSpot, "https://api.hubapi.com") with
            {
                Platform = new DataConnectorPlatformDefinition(
                    "lakehold.hubspot-contacts",
                    1,
                    DataConnectorReadMode.Incremental,
                    DataConnectorSchemaPolicy.Reject,
                    ["id"],
                    [],
                    new DataConnectorSourceSettings(PageSize: 100, Properties: ["email"]),
                    new DataConnectorAuthentication(
                        DataConnectorAuthenticationKind.OAuthRefreshToken,
                        ClientIdSecretReference: $"env://{clientId}",
                        ClientSecretReference: $"env://{clientSecret}",
                        RefreshTokenSecretReference: $"env://{refreshToken}")),
            };
            var connector = DataConnector.Create(1, 1, definition, clock.GetUtcNow());

            var result = await source.ReadAsync(
                Context(connector, "2026-08-01T00:00:00.0000000+00:00"),
                snapshot,
                default);

            Assert.Equal(1, snapshot.Rows);
            Assert.True(DateTimeOffset.Parse(result.ProposedCheckpoint!) <= threshold);
            Assert.InRange(handler.SearchRequests, 3, 55);
            Assert.True(handler.ObservedOversizedWindow);
            Assert.True(handler.ObservedBoundedWindow);
        }
        finally
        {
            Environment.SetEnvironmentVariable(clientId, null);
            Environment.SetEnvironmentVariable(clientSecret, null);
            Environment.SetEnvironmentVariable(refreshToken, null);
        }
    }

    [SkippableFact]
    public async Task PostgreSql_adapter_reads_only_rows_after_the_typed_checkpoint()
    {
        var configured = Environment.GetEnvironmentVariable("LAKEHOLD_TEST_POSTGRES");
        Skip.If(string.IsNullOrWhiteSpace(configured), "Set LAKEHOLD_TEST_POSTGRES to run PostgreSQL adapter tests.");
        var connection = new NpgsqlConnectionStringBuilder(configured!);
        Skip.If(
            string.IsNullOrWhiteSpace(connection.Host)
            || connection.Host.Contains('/', StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(connection.Username)
            || string.IsNullOrWhiteSpace(connection.Password),
            "The PostgreSQL adapter test requires TCP host, username, and password settings.");
        var schema = "lh_source_" + Guid.NewGuid().ToString("N");
        await using var administrative = new NpgsqlConnection(configured);
        await administrative.OpenAsync();
        await using (var create = administrative.CreateCommand())
        {
            create.CommandText = $"CREATE SCHEMA {schema}; "
                                 + $"CREATE TABLE {schema}.orders (id BIGINT PRIMARY KEY, name TEXT NOT NULL); "
                                 + $"INSERT INTO {schema}.orders VALUES (1, 'old'), (2, 'new'), (3, 'newest')";
            await create.ExecuteNonQueryAsync();
        }

        var userVariable = $"LAKEHOLD_PG_USER_{Guid.NewGuid():N}";
        var passwordVariable = $"LAKEHOLD_PG_PASSWORD_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(userVariable, connection.Username);
        Environment.SetEnvironmentVariable(passwordVariable, connection.Password);
        try
        {
            var options = TestOptions();
            Bind(options, $"env://{userVariable}", connection.Host);
            Bind(options, $"env://{passwordVariable}", connection.Host);
            using var scratch = new ConnectorScratchSpace(options, TimeProvider.System);
            await using var snapshot = await ConnectorSnapshotFile.CreateAsync(scratch, options, default);
            var source = new PostgreSqlDataConnectorSource(options, SecretResolver(options));
            var endpoint = new UriBuilder("postgresql", connection.Host, connection.Port, connection.Database)
                .Uri.AbsoluteUri;
            var definition = Definition(DataConnectorKind.PostgreSql, endpoint) with
            {
                Platform = new DataConnectorPlatformDefinition(
                    "lakehold.postgresql",
                    1,
                    DataConnectorReadMode.Incremental,
                    DataConnectorSchemaPolicy.Reject,
                    ["id"],
                    [],
                    new DataConnectorSourceSettings(
                        $"{schema}.orders",
                        "id",
                        "int64",
                        100,
                        CursorIsCommitMonotonic: true),
                    new DataConnectorAuthentication(
                        DataConnectorAuthenticationKind.PostgreSqlPassword,
                        UsernameSecretReference: $"env://{userVariable}",
                        PasswordSecretReference: $"env://{passwordVariable}")),
            };
            var connector = DataConnector.Create(1, 1, definition, DateTimeOffset.UtcNow);

            var result = await source.ReadAsync(Context(connector, "1"), snapshot, default);
            await snapshot.SealAsync(default);

            Assert.Equal(2, snapshot.Rows);
            Assert.Equal("3", result.ProposedCheckpoint);
            var records = await File.ReadAllLinesAsync(snapshot.Path);
            Assert.DoesNotContain(records, record => record.Contains("\"id\":1", StringComparison.Ordinal));
            Assert.Contains(records, record => record.Contains("\"id\":3", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(userVariable, null);
            Environment.SetEnvironmentVariable(passwordVariable, null);
            await using var drop = administrative.CreateCommand();
            drop.CommandText = $"DROP SCHEMA {schema} CASCADE";
            await drop.ExecuteNonQueryAsync();
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
            HubSpotMinimumRequestInterval = TimeSpan.Zero,
            HubSpotIndexingDelay = TimeSpan.Zero,
            HubSpotCheckpointOverlap = TimeSpan.Zero,
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

    private static ConnectorReadContext Context(DataConnector connector, string? checkpoint) =>
        new(connector, checkpoint, "test-tenant", "test-catalog");

    private static void Bind(
        IOptions<ConnectorOptions> options,
        string reference,
        string destinationHost,
        string tenantSlug = "test-tenant",
        string catalogName = "test-catalog") => options.Value.SecretBindings =
        [
            .. options.Value.SecretBindings,
            new ConnectorSecretBindingOptions
            {
                TenantSlug = tenantSlug,
                CatalogName = catalogName,
                Reference = reference,
                DestinationHost = destinationHost,
            },
        ];

    private static ConnectorSecretResolver SecretResolver(IOptions<ConnectorOptions> options) => new(
        [new EnvironmentConnectorSecretProvider()],
        options);

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class HubSpotHandler : HttpMessageHandler
    {
        public string? TokenBody { get; private set; }

        public string? SearchBody { get; private set; }

        public string? SearchAuthorization { get; private set; }

        public int SearchRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/v1/token")
            {
                TokenBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"renewed-access-token\"}"),
                };
            }

            SearchAuthorization = request.Headers.Authorization?.ToString();
            SearchBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            SearchRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SearchRequests switch
                {
                    1 => "{\"total\":2,\"results\":[{\"id\":\"50\",\"updatedAt\":\"2026-08-02T10:00:00.0000000+00:00\",\"properties\":{\"email\":\"grace@example.test\"}}]}",
                    2 => "{\"total\":2,\"results\":[{\"id\":\"50\",\"updatedAt\":\"2026-08-02T10:00:00.0000000+00:00\",\"properties\":{\"email\":\"grace@example.test\"}}],\"paging\":{\"next\":{\"after\":\"page-2\"}}}",
                    _ => "{\"total\":2,\"results\":[{\"id\":\"51\",\"updatedAt\":\"2026-08-02T10:30:00.0000000+00:00\",\"properties\":{\"email\":\"ada@example.test\"}}]}",
                }),
            };
        }
    }

    private sealed class HubSpotWindowHandler(DateTimeOffset threshold) : HttpMessageHandler
    {
        public int SearchRequests { get; private set; }

        public bool ObservedOversizedWindow { get; private set; }

        public bool ObservedBoundedWindow { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/v1/token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"window-access-token\"}"),
                };
            }

            SearchRequests++;
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            var upperMilliseconds = body.RootElement.GetProperty("filterGroups")[0]
                .GetProperty("filters")
                .EnumerateArray()
                .Single(filter => filter.GetProperty("operator").GetString() == "LTE")
                .GetProperty("value")
                .GetString();
            var upper = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(upperMilliseconds!));
            var total = upper > threshold ? 10_000 : 1;
            ObservedOversizedWindow |= total > 9_000;
            ObservedBoundedWindow |= total <= 9_000;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    total,
                    results = new[]
                    {
                        new
                        {
                            id = "50",
                            updatedAt = "2026-08-01T10:00:00.0000000+00:00",
                            properties = new { email = "grace@example.test" },
                        },
                    },
                })),
            };
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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

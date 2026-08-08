using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.PublicApi;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class PublicApiContractTests
{
    [Fact]
    public async Task Canonical_failure_is_an_RFC_9457_problem_with_stable_code()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.GetTestClient().GetAsync("/api/v1/failure");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("unsafe input", problem.GetProperty("detail").GetString());
        Assert.Equal("/api/v1/failure", problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("requestId").GetString()));
        Assert.Equal(
            problem.GetProperty("requestId").GetString(),
            response.Headers.GetValues(PublicApiCorrelationExtensions.HeaderName).Single());
    }

    [Theory]
    [InlineData("/api/v1/revise", "revision")]
    [InlineData("/api/v1/revise?revision=not-a-number", "revision")]
    public async Task An_unbindable_request_is_a_client_error_not_a_server_failure(
        string path,
        string expectedParameter)
    {
        // Both of these are the caller's mistake, and both used to answer 500 — which told an SDK
        // the server had failed and that retrying was reasonable, when no retry could ever succeed.
        await using var application = await CreateBindingApplicationAsync();
        using var response = await application.GetTestClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(path.Split('?')[0], problem.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("requestId").GetString()));

        // The detail names the parameter, because "bad request" alone leaves the caller guessing
        // which of several a handler takes.
        Assert.Contains(
            expectedParameter,
            problem.GetProperty("detail").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bound_request_still_reaches_its_handler()
    {
        // The guard above must not swallow requests that are fine; without this, a handler that
        // returned 400 for everything would satisfy the theory.
        await using var application = await CreateBindingApplicationAsync();
        using var response = await application.GetTestClient().GetAsync("/api/v1/revise?revision=7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("revision 7", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Legacy_route_reaches_v1_and_advertises_its_sunset()
    {
        await using var application = await CreateApplicationAsync();
        using var response = await application.GetTestClient().GetAsync("/api/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("pong", await response.Content.ReadAsStringAsync());
        Assert.Equal("true", response.Headers.GetValues("Deprecation").Single());
        Assert.Equal(PublicApiRoutes.Sunset, response.Headers.GetValues("Sunset").Single());
        Assert.Contains(PublicApiRoutes.OpenApiPath, response.Headers.GetValues("Link").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_discovery_is_stable_and_canonical()
    {
        await using var application = await CreateApplicationAsync();
        var capabilities = await application.GetTestClient()
            .GetFromJsonAsync<PublicApiCapabilities>("/api/v1/capabilities");

        Assert.NotNull(capabilities);
        Assert.Equal("LakeHold", capabilities.Product);
        Assert.Equal("v1", capabilities.ApiVersion);
        Assert.Contains("connectors", capabilities.Features);
        Assert.Equal(PublicApiRoutes.OpenApiPath, capabilities.Links.OpenApi);
    }

    [Theory]
    [InlineData("GET", "api/v1/tenants/{tenantSlug}", "get_api_v1_tenants_tenantSlug")]
    [InlineData("POST", "api/v1/tenants/{tenantSlug}/catalogs", "post_api_v1_tenants_tenantSlug_catalogs")]
    public void Generated_operation_identifiers_are_deterministic(
        string method,
        string path,
        string expected)
        => Assert.Equal(expected, PublicApiOperationIds.Create(method, path));

    [Fact]
    public void Additive_access_capabilities_remain_optional_in_the_public_schema()
    {
        var accessSchema = new OpenApiSchema
        {
            Required = new HashSet<string>(StringComparer.Ordinal)
            {
                "mode",
                "tenantAdmin",
            },
        };
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["AccessDto"] = accessSchema,
                },
            },
        };

        PublicApiOpenApi.PreserveAdditiveAccessCompatibility(document);

        Assert.Contains("mode", accessSchema.Required);
        Assert.DoesNotContain("tenantAdmin", accessSchema.Required);
    }

    [Fact]
    public void Additive_connector_capabilities_remain_optional_in_the_public_schema()
    {
        var authentication = RequiredSchema(
            "kind",
            "schemaRegistryUsernameSecretReference",
            "schemaRegistryPasswordSecretReference");
        var connector = RequiredSchema(
            "id",
            "sourceAcknowledgementPendingUtc",
            "sourceAcknowledgementError");
        var sourceSettings = RequiredSchema(
            "pageSize",
            "kafkaBootstrapServers",
            "kafkaTopic",
            "kafkaConsumerGroup",
            "schemaRegistryUrl");
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["DataConnectorAuthenticationDto"] = authentication,
                    ["DataConnectorDto"] = connector,
                    ["DataConnectorSourceSettingsDto"] = sourceSettings,
                },
            },
        };

        PublicApiOpenApi.PreserveAdditiveConnectorCompatibility(document);

        Assert.Equal(["kind"], authentication.Required);
        Assert.Equal(["id"], connector.Required);
        Assert.Equal(["pageSize"], sourceSettings.Required);
    }

    [Fact]
    public void Additive_system_settings_input_remains_optional_in_the_public_schema()
    {
        var response = RequiredSchema(
            "mcpEnabled",
            "mcpAllowWrites",
            "mcpAllowOperatorCommands",
            "version");
        var request = RequiredSchema(
            "mcpEnabled",
            "mcpAllowWrites",
            "mcpAllowOperatorCommands",
            "version");
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["SystemSettingsDto"] = response,
                    ["UpdateSystemSettingsRequest"] = request,
                },
            },
        };

        PublicApiOpenApi.PreserveAdditiveSystemSettingsCompatibility(document);

        Assert.Equal(["mcpEnabled", "mcpAllowWrites", "version"], response.Required);
        Assert.Equal(["mcpEnabled", "mcpAllowWrites", "version"], request.Required);
    }

    [Fact]
    public void Additive_query_audit_fields_remain_optional_in_the_public_schema()
    {
        var queryRun = RequiredSchema(
            "id",
            "memberId",
            "actorKind",
            "actorName",
            "origin");
        var document = new OpenApiDocument
        {
            Components = new OpenApiComponents
            {
                Schemas = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                {
                    ["QueryRunDto"] = queryRun,
                },
            },
        };

        PublicApiOpenApi.PreserveAdditiveQueryAuditCompatibility(document);

        Assert.Equal(["id"], queryRun.Required);
    }

    private static OpenApiSchema RequiredSchema(params string[] properties) => new()
    {
        Required = new HashSet<string>(properties, StringComparer.Ordinal),
    };

    [Fact]
    public async Task Mutation_response_is_persisted_and_replayed_without_reexecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-idempotency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<ControlPlaneContext>(options =>
                options.UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}"));
            builder.Services.AddScoped<PublicApiIdempotencyStore>();
            builder.Services.AddSingleton(TimeProvider.System);

            await using var app = builder.Build();
            app.UseLegacyApiCompatibility();
            app.UseRouting();
            app.UsePublicApiRequestHashing();
            var executions = 0;
            app.MapGroup(PublicApiRoutes.BasePath)
                .AddEndpointFilter(StubPrincipal)
                .MapPost("/mutations", () => Results.Created(
                    "/api/v1/mutations/one",
                    new { execution = ++executions }))
                .WithIdempotency();

            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>()
                    .Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "0123456789abcdef");

            using var first = await client.PostAsJsonAsync("/api/v1/mutations", new { value = 1 });
            using var second = await client.PostAsJsonAsync("/api/v1/mutations", new { value = 1 });

            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            Assert.Equal(HttpStatusCode.Created, second.StatusCode);
            Assert.Equal(1, executions);
            Assert.Equal("true", second.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(first.Headers.Location, second.Headers.Location);
            Assert.Equal(
                await first.Content.ReadAsStringAsync(),
                await second.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reusing_a_key_with_different_input_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-idempotency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<ControlPlaneContext>(options =>
                options.UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}"));
            builder.Services.AddScoped<PublicApiIdempotencyStore>();
            builder.Services.AddSingleton(TimeProvider.System);

            await using var app = builder.Build();
            app.UseRouting();
            app.UsePublicApiRequestHashing();
            app.MapGroup(PublicApiRoutes.BasePath)
                .AddEndpointFilter(StubPrincipal)
                .MapPost("/mutations", () => Results.NoContent())
                .WithIdempotency();

            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>()
                    .Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "fedcba9876543210");
            using var first = await client.PostAsJsonAsync("/api/v1/mutations", new { value = 1 });
            using var conflict = await client.PostAsJsonAsync("/api/v1/mutations", new { value = 2 });

            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("idempotency_key_reused", problem.GetProperty("code").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_content_type_is_refused()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-idempotency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddDbContext<ControlPlaneContext>(options =>
                options.UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}"));
            builder.Services.AddScoped<PublicApiIdempotencyStore>();
            builder.Services.AddSingleton(TimeProvider.System);

            await using var app = builder.Build();
            app.UseRouting();
            app.UsePublicApiRequestHashing();
            app.MapGroup(PublicApiRoutes.BasePath)
                .AddEndpointFilter(StubPrincipal)
                .MapPost("/mutations", () => Results.NoContent())
                .WithIdempotency();

            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ControlPlaneContext>()
                    .Database.EnsureCreatedAsync();
            }

            await app.StartAsync();
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "content-type-key");
            using var first = await client.PostAsync(
                "/api/v1/mutations",
                new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
            using var conflict = await client.PostAsync(
                "/api/v1/mutations",
                new StringContent("{}", System.Text.Encoding.UTF8, "text/plain"));

            Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task One_time_secret_response_cannot_be_combined_with_idempotency()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapPost("/credential", () => Results.Ok(new { token = "secret" }))
            .WithOneTimeSecretResponse()
            .WithIdempotency();

        await app.StartAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var response = await app.GetTestClient().PostAsync(
                "/credential",
                content: null);
        });
        Assert.Contains("one-time secret response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Idempotent_response_buffer_refuses_unbounded_output()
    {
        using var buffer = new BoundedMemoryStream(IdempotentMutationExtensions.MaximumResponseBytes);
        var oversized = new byte[IdempotentMutationExtensions.MaximumResponseBytes + 1];

        var exception = Assert.Throws<InvalidOperationException>(() => buffer.Write(oversized));

        Assert.Contains("1 MiB", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, buffer.Length);
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789abcde ", false)]
    [InlineData("0123456789abcde\t", false)]
    [InlineData("0123456789abcdeé", false)]
    public void Idempotency_keys_use_one_portable_visible_ascii_contract(string value, bool expected)
        => Assert.Equal(expected, IdempotentMutationExtensions.IsValidKey(value));

    [Fact]
    public void Oidc_users_have_distinct_non_identifying_idempotency_scopes()
    {
        var alice = OidcContext("alice");
        var bob = OidcContext("bob");
        var principal = new LakeholdPrincipal(
            Scope: TokenScope.Tenant,
            TenantId: null,
            TenantSlug: "acme",
            CatalogName: null,
            IsReadOnly: false,
            TokenId: null);

        var aliceScope = PublicApiIdempotencyFilter.CreateScope(alice, principal);
        var repeatedAliceScope = PublicApiIdempotencyFilter.CreateScope(alice, principal);
        var bobScope = PublicApiIdempotencyFilter.CreateScope(bob, principal);

        Assert.Equal(aliceScope, repeatedAliceScope);
        Assert.NotEqual(aliceScope, bobScope);
        Assert.DoesNotContain("alice", aliceScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Durable_operation_claims_once_and_persists_its_result()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}")
                .Options;
            await using var context = new ControlPlaneContext(options);
            await context.Database.EnsureCreatedAsync();
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 2, 23, 0, 0, TimeSpan.Zero));
            var store = new PublicApiOperationStore(context, clock);

            var queued = await store.EnqueueAsync(
                "acme",
                "analytics",
                PublicApiOperationKinds.Eject,
                new EjectOperationRequest(false),
                tokenId: 42,
                cancellationToken: default);
            var claimed = await store.ClaimNextAsync("node-a", TimeSpan.FromMinutes(5), default);

            Assert.NotNull(claimed);
            Assert.Equal(queued.Id, claimed.Id);
            Assert.Equal(ApiOperationStatus.Running, claimed.Status);
            Assert.True(await store.RenewLeaseAsync(
                claimed.Id, claimed.LeaseOwner!, TimeSpan.FromMinutes(5), default));
            Assert.Null(await store.ClaimNextAsync("node-b", TimeSpan.FromMinutes(5), default));

            await store.CompleteAsync(claimed, new { verified = true }, default);
            var completed = await store.GetAsync(queued.Id, default);
            Assert.NotNull(completed);
            Assert.Equal(ApiOperationStatus.Succeeded, completed.Status);
            Assert.Contains("verified", completed.ResultJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Expired_running_operation_becomes_indeterminate_and_is_not_reexecuted()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}")
                .Options;
            await using var context = new ControlPlaneContext(options);
            await context.Database.EnsureCreatedAsync();
            var now = new DateTimeOffset(2026, 8, 2, 23, 0, 0, TimeSpan.Zero);
            context.ApiOperations.Add(new ApiOperation
            {
                Id = Guid.NewGuid().ToString("N"),
                TenantSlug = "acme",
                CatalogName = "analytics",
                Kind = PublicApiOperationKinds.Eject,
                RequestJson = "{}",
                Status = ApiOperationStatus.Running,
                CreatedUtc = now.AddHours(-1),
                StartedUtc = now.AddHours(-1),
                LeaseOwner = "lost-node",
                LeaseExpiresUtc = now.AddMinutes(-1),
            });
            await context.SaveChangesAsync();
            var store = new PublicApiOperationStore(context, new FixedTimeProvider(now));

            Assert.Equal(1, await store.MarkExpiredLeasesIndeterminateAsync(default));
            Assert.Null(await store.ClaimNextAsync("node-b", TimeSpan.FromMinutes(5), default));
            var operation = await context.ApiOperations.AsNoTracking().SingleAsync();
            Assert.Equal(ApiOperationStatus.Indeterminate, operation.Status);
            Assert.NotNull(operation.CompletedUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Expired_operation_lease_cannot_be_renewed_or_completed()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-operations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}")
                .Options;
            await using var context = new ControlPlaneContext(options);
            await context.Database.EnsureCreatedAsync();
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 8, 2, 23, 0, 0, TimeSpan.Zero));
            var store = new PublicApiOperationStore(context, clock);
            var queued = await store.EnqueueAsync(
                "acme",
                "analytics",
                PublicApiOperationKinds.Eject,
                new EjectOperationRequest(false),
                tokenId: 42,
                cancellationToken: default);
            var claimed = await store.ClaimNextAsync("node-a", TimeSpan.FromMinutes(5), default);
            Assert.NotNull(claimed);

            clock.Advance(TimeSpan.FromMinutes(5));

            Assert.False(await store.RenewLeaseAsync(
                claimed.Id, claimed.LeaseOwner!, TimeSpan.FromMinutes(5), default));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.CompleteAsync(claimed, new { verified = true }, default));
            Assert.Equal(1, await store.MarkExpiredLeasesIndeterminateAsync(default));
            Assert.Equal(ApiOperationStatus.Indeterminate, (await store.GetAsync(queued.Id, default))!.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Retention_deletes_only_expired_completed_coordination_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "lakehold-retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);
            var options = new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "control.duckdb")}")
                .Options;
            await using var context = new ControlPlaneContext(options);
            await context.Database.EnsureCreatedAsync();
            context.ApiIdempotencyRecords.AddRange(
                Idempotency(ApiIdempotencyStatus.Completed, now.AddDays(-8)),
                Idempotency(ApiIdempotencyStatus.Completed, now.AddDays(-1)),
                Idempotency(ApiIdempotencyStatus.InProgress, now.AddDays(-8)));
            context.ApiOperations.AddRange(
                Operation(ApiOperationStatus.Succeeded, now.AddDays(-31)),
                Operation(ApiOperationStatus.Failed, now.AddDays(-1)),
                Operation(ApiOperationStatus.Running, now.AddDays(-31)));
            await context.SaveChangesAsync();

            var clock = new FixedTimeProvider(now);
            Assert.Equal(1, await new PublicApiIdempotencyStore(context, clock)
                .DeleteExpiredCompletedAsync(default));
            Assert.Equal(1, await new PublicApiOperationStore(context, clock)
                .DeleteExpiredTerminalAsync(default));

            Assert.Equal(2, await context.ApiIdempotencyRecords.CountAsync());
            Assert.Equal(2, await context.ApiOperations.CountAsync());
            Assert.Contains(
                await context.ApiIdempotencyRecords.Select(record => record.Status).ToListAsync(),
                status => status == ApiIdempotencyStatus.InProgress);
            Assert.Contains(
                await context.ApiOperations.Select(operation => operation.Status).ToListAsync(),
                status => status == ApiOperationStatus.Running);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static ApiIdempotencyRecord Idempotency(ApiIdempotencyStatus status, DateTimeOffset createdUtc)
            => new()
            {
                Scope = Guid.NewGuid().ToString("N"),
                KeyHash = new string('A', 64),
                RequestHash = new string('B', 64),
                Status = status,
                ResponseStatusCode = status == ApiIdempotencyStatus.Completed ? 200 : null,
                ResponseBody = status == ApiIdempotencyStatus.Completed ? [] : null,
                CreatedUtc = createdUtc,
                CompletedUtc = status == ApiIdempotencyStatus.Completed ? createdUtc : null,
            };

        static ApiOperation Operation(ApiOperationStatus status, DateTimeOffset createdUtc)
            => new()
            {
                Id = Guid.NewGuid().ToString("N"),
                TenantSlug = "acme",
                CatalogName = "analytics",
                Kind = PublicApiOperationKinds.Eject,
                RequestJson = "{}",
                Status = status,
                CreatedUtc = createdUtc,
                CompletedUtc = status is ApiOperationStatus.Succeeded or ApiOperationStatus.Failed
                    ? createdUtc
                    : null,
            };
    }

    [Fact]
    public void Durable_operation_visibility_honours_instance_tenant_and_catalog_scope()
    {
        var operation = new ApiOperation
        {
            Id = "operation-1",
            TenantSlug = "acme",
            CatalogName = "analytics",
            Kind = PublicApiOperationKinds.Eject,
            RequestJson = "{}",
        };
        var instance = Principal(TokenScope.Instance, tenant: null, catalog: null);
        var tenant = Principal(TokenScope.Tenant, tenant: "acme", catalog: null);
        var catalog = Principal(TokenScope.Tenant, tenant: "acme", catalog: "analytics");
        var otherTenant = Principal(TokenScope.Tenant, tenant: "other", catalog: null);
        var otherCatalog = Principal(TokenScope.Tenant, tenant: "acme", catalog: "other");

        Assert.True(PublicApiOperationEndpoints.IsVisibleTo(instance, operation));
        Assert.True(PublicApiOperationEndpoints.IsVisibleTo(tenant, operation));
        Assert.True(PublicApiOperationEndpoints.IsVisibleTo(catalog, operation));
        Assert.False(PublicApiOperationEndpoints.IsVisibleTo(otherTenant, operation));
        Assert.False(PublicApiOperationEndpoints.IsVisibleTo(otherCatalog, operation));
    }

    [Fact]
    public async Task Canonical_lists_use_an_opaque_cursor_envelope()
    {
        await using var application = await CreatePaginationApplicationAsync();
        var client = application.GetTestClient();

        using var firstResponse = await client.GetAsync("/api/v1/widgets?limit=2");
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["one", "two"], first.GetProperty("items").EnumerateArray().Select(item => item.GetString()));
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        using var secondResponse = await client.GetAsync(
            $"/api/v1/widgets?limit=2&cursor={Uri.EscapeDataString(cursor!)}");
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["three"], second.GetProperty("items").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Cursor_is_bound_to_the_original_request()
    {
        await using var application = await CreatePaginationApplicationAsync();
        var client = application.GetTestClient();
        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/widgets?limit=2");
        var cursor = first.GetProperty("nextCursor").GetString();

        using var response = await client.GetAsync(
            $"/api/v1/widgets?limit=3&cursor={Uri.EscapeDataString(cursor!)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_cursor", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Legacy_lists_retain_their_array_shape_during_the_compatibility_window()
    {
        await using var application = await CreatePaginationApplicationAsync();
        var response = await application.GetTestClient().GetFromJsonAsync<JsonElement>("/api/widgets?limit=2");

        Assert.Equal(JsonValueKind.Array, response.ValueKind);
        Assert.Equal(3, response.GetArrayLength());
    }

    [Fact]
    public async Task Source_window_pagination_does_not_silently_truncate_after_the_first_500_items()
    {
        await using var application = await CreatePaginationApplicationAsync();
        var client = application.GetTestClient();

        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/many-widgets?limit=500");
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.Equal(500, first.GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        var second = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/many-widgets?limit=500&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(150, second.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    /// <summary>
    ///     Stands in for the authorization filter, which these harnesses do not mount. Reading a
    ///     principal without one now throws rather than yielding a route-trusting default, so a
    ///     pipeline that exercises idempotency has to supply the identity it scopes by.
    /// </summary>
    private static ValueTask<object?> StubPrincipal(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        context.HttpContext.Items[LakeholdAuthorizationFilter.PrincipalItemKey] = new LakeholdPrincipal(
            Scope: TokenScope.Tenant,
            TenantId: 1,
            TenantSlug: "demo",
            CatalogName: null,
            IsReadOnly: false,
            TokenId: 1);
        return next(context);
    }

    /// <summary>
    ///     A minimal app wired the way `Program` wires it — problem details plus the exception
    ///     handler — with one route that takes a required, non-nullable query parameter. That is the
    ///     shape every optimistic-concurrency route uses (`?revision=`), and the shape that produced
    ///     a 500 when the parameter was missing or unparseable.
    /// </summary>
    private static async Task<WebApplication> CreateBindingApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                PublicApiProblems.Enrich(context.ProblemDetails, context.HttpContext);
        });

        var app = builder.Build();
        app.UsePublicApiCorrelation();
        app.UseExceptionHandler();
        app.UseRouting();

        app.MapGroup(PublicApiRoutes.BasePath)
            .MapGet("/revise", (int revision) => Results.Text($"revision {revision}"));

        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseLegacyApiCompatibility();
        app.UsePublicApiCorrelation();
        app.UseRouting();

        var api = app.MapGroup(PublicApiRoutes.BasePath)
            .AddEndpointFilter<PublicApiProblemFilter>();
        api.MapPublicApiDiscovery();
        api.MapGet("/ping", () => Results.Text("pong"));
        api.MapGet("/failure", () => Results.BadRequest("unsafe input"));

        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> CreatePaginationApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();

        var app = builder.Build();
        app.UseLegacyApiCompatibility();
        app.UseRouting();
        app.MapGroup(PublicApiRoutes.BasePath)
            .MapGet("/widgets", () => Results.Ok<IReadOnlyList<string>>(["one", "two", "three"]))
            .WithCursorPagination<string>();
        app.MapGroup(PublicApiRoutes.BasePath)
            .MapGet("/many-widgets", (HttpContext http) => Results.Ok<IReadOnlyList<int>>(
                [.. PublicApiPagination.ApplySourceWindow(Enumerable.Range(1, 650).AsQueryable(), http)]))
            .WithCursorPagination<int>();

        await app.StartAsync();
        return app;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow = _utcNow.Add(elapsed);
    }

    private static LakeholdPrincipal Principal(TokenScope scope, string? tenant, string? catalog)
        => new(
            Scope: scope,
            TenantId: null,
            TenantSlug: tenant,
            CatalogName: catalog,
            IsReadOnly: false,
            TokenId: 1);

    private static DefaultHttpContext OidcContext(string subject)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "oidc")),
        };
        context.Request.Method = HttpMethod.Post.Method;
        context.Request.Path = "/api/v1/tenants/acme/catalogs/analytics/connectors";
        return context;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Endpoints;
using Lakehold.Api.Mcp;
using Lakehold.Api.PublicApi;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Lakehold.Engine.Catalog;
using Lakehold.Engine.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the redacted storage-configuration read. Two things have to hold and neither is
///     visible in the handler alone: that the route is reachable by an instance credential and nobody
///     else, and that no credential can travel on it.
/// </summary>
/// <remarks>
///     <para>
///         Exercised over a real pipeline rather than by calling the handler, because the authorization
///         claim is about where the route is <em>mapped</em>. The handler is a pure projection and
///         cannot refuse anyone; what refuses is the group's filter and capability, and a future edit
///         that moved this route out of that group would leave a direct-call test green while
///         publishing the deployment's profile inventory to any tenant token.
///     </para>
///     <para>
///         The redaction tests read the response body as text and search it for the sentinel values
///         configured into the profiles. Asserting on DTO members instead would prove only the members
///         that exist today: a serialised secret added to either record later — or reached through a
///         nested type — is caught by the bytes and by nothing else.
///     </para>
/// </remarks>
public sealed class SystemStorageEndpointTests : IAsyncLifetime
{
    private const string S3Key = "SENTINEL-s3-key-id";
    private const string S3Secret = "SENTINEL-s3-secret";
    private const string S3SessionToken = "SENTINEL-s3-session-token";
    private const string AzureConnectionString = "SENTINEL-azure-connection-string";
    private const string AzureAccountName = "SENTINEL-azure-account";
    private const string AzureChain = "SENTINEL-azure-chain";

    private static readonly string[] Sentinels =
    [
        S3Key, S3Secret, S3SessionToken, AzureConnectionString, AzureAccountName, AzureChain,
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lakehold-system-storage", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private string _instanceToken = null!;
    private string _tenantToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ControlPlaneContext>(
            options => options.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        builder.Services.AddScoped<ApiTokenAuthenticator>();
        builder.Services.AddScoped<MemberDirectory>();
        // The group's other two routes take this; unregistered, minimal APIs infer it as a request
        // body and the whole group fails to build.
        builder.Services.AddScoped<McpRuntimeSettingsStore>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<LakeholdAuthOptions>(options =>
        {
            // No demo catalog: this surface is instance-scoped, so there is no anonymous path to it.
            options.DemoTenant = string.Empty;
            options.DemoCatalog = string.Empty;
        });
        builder.Services.Configure<LakehouseOptions>(Configure);

        _app = builder.Build();
        _app.UseRouting();
        _app.MapGroup(PublicApiRoutes.BasePath).MapSystemSettingsEndpoints();

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
            await context.Database.EnsureCreatedAsync();

            var tenant = new Tenant
            {
                Slug = "acme",
                DisplayName = "Acme",
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var instance = ApiTokenFactory.Issue(TokenScope.Instance, tenant: null, "admin", now);
            // An owner, so a refusal cannot be mistaken for the tenant simply lacking a role.
            var owner = ApiTokenFactory.Issue(
                TokenScope.Tenant, tenant, "owner", now, role: TokenRole.Owner);
            context.ApiTokens.AddRange(instance.Record, owner.Record);
            await context.SaveChangesAsync();

            _instanceToken = instance.Plaintext;
            _tenantToken = owner.Plaintext;
        }

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    private static void Configure(LakehouseOptions options)
    {
        options.DataRoot = "s3://company-lake/lakehold";
        options.BackupRoot = "s3://company-backups/lakehold";
        options.EjectRoot = "s3://company-exports/lakehold";
        options.DefaultStorageProfile = "primary";

        options.StorageProfiles["primary"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.S3,
            KeyId = S3Key,
            Secret = S3Secret,
            SessionToken = S3SessionToken,
            Region = "eu-west-1",
        };
        options.StorageProfiles["compatible"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.S3,
            KeyId = S3Key,
            Secret = S3Secret,
            Endpoint = "minio.example.com:9000",
            UseSsl = false,
            UrlStyle = "path",
        };
        options.StorageProfiles["half-configured"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.Gcs,
            KeyId = S3Key,
        };
        options.StorageProfiles["azure-string"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.Azure,
            AzureConnectionString = AzureConnectionString,
        };
        options.StorageProfiles["azure-identity"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.Azure,
            AzureAccountName = AzureAccountName,
            AzureCredentialChain = AzureChain,
        };
        options.StorageProfiles["azure-empty"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.Azure,
        };
        options.StorageProfiles["on-disk"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.Local,
        };
    }

    private HttpClient Client(string? bearer)
    {
        var client = _app.GetTestClient();
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    private async Task<SystemStorageDto> ReadAsync()
    {
        var storage = await Client(_instanceToken)
            .GetFromJsonAsync<SystemStorageDto>("/api/v1/system-settings/storage");
        Assert.NotNull(storage);
        return storage;
    }

    private static StorageProfileSummaryDto Profile(SystemStorageDto storage, string name)
        => Assert.Single(storage.Profiles, profile => profile.Name == name);

    [Fact]
    public async Task An_instance_credential_reads_the_deployments_placement()
    {
        var storage = await ReadAsync();

        Assert.Equal("s3://company-lake/lakehold", storage.DataRoot);
        Assert.Equal("s3://company-backups/lakehold", storage.BackupRoot);
        Assert.Equal("s3://company-exports/lakehold", storage.EjectRoot);
        Assert.Equal("primary", storage.DefaultStorageProfile);

        // Not a placeholder for a future toggle: the options are bound at startup, so a UI that
        // implied an editable value here would be describing something that cannot happen.
        Assert.True(storage.RequiresRestartToChange);
    }

    [Fact]
    public async Task A_tenant_credential_is_forbidden_and_an_anonymous_one_unauthorized()
    {
        using var tenant = await Client(_tenantToken).GetAsync("/api/v1/system-settings/storage");
        using var anonymous = await Client(bearer: null).GetAsync("/api/v1/system-settings/storage");

        // 403 rather than 404 is correct here and not a slip against invariant 19: the instance
        // surface addresses no tenant, so refusing it confirms nothing about who exists.
        Assert.Equal(HttpStatusCode.Forbidden, tenant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task No_configured_credential_appears_anywhere_in_the_response()
    {
        using var response = await Client(_instanceToken).GetAsync("/api/v1/system-settings/storage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var sentinel in Sentinels)
        {
            Assert.DoesNotContain(sentinel, body, StringComparison.OrdinalIgnoreCase);
        }

        // The sentinels have to be reachable for the scan above to mean anything: a profile inventory
        // that came back empty would pass it while proving nothing.
        Assert.Equal(7, JsonDocument.Parse(body).RootElement.GetProperty("profiles").GetArrayLength());
    }

    [Fact]
    public void No_response_member_is_named_for_a_credential()
    {
        // The bytes are the real guard; this one names the mistake, so a member added as `KeyId` or
        // `AzureAccountName` fails with a message about what it is rather than about a missing string.
        string[] forbidden = ["Secret", "Key", "Token", "Password", "Credential", "Account", "Chain"];
        var members = typeof(SystemStorageDto).GetProperties()
            .Concat(typeof(StorageProfileSummaryDto).GetProperties())
            .Select(property => property.Name)
            .Where(name => name is not nameof(StorageProfileSummaryDto.CredentialsConfigured)
                and not nameof(StorageProfileSummaryDto.AzureAuthentication))
            .ToArray();

        Assert.All(members, name => Assert.DoesNotContain(
            forbidden,
            word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task A_profile_carries_what_selecting_it_needs_and_nothing_else()
    {
        var storage = await ReadAsync();

        var primary = Profile(storage, "primary");
        Assert.Equal("S3", primary.Kind);
        Assert.Equal("eu-west-1", primary.Region);
        Assert.Null(primary.Endpoint);
        Assert.True(primary.UseSsl);
        Assert.Equal("vhost", primary.UrlStyle);
        Assert.Null(primary.AzureAuthentication);

        var compatible = Profile(storage, "compatible");
        Assert.Equal("minio.example.com:9000", compatible.Endpoint);
        Assert.False(compatible.UseSsl);
        Assert.Equal("path", compatible.UrlStyle);
        Assert.Null(compatible.Region);
    }

    [Fact]
    public async Task Credential_state_is_reported_as_the_secret_builder_would_find_it()
    {
        var storage = await ReadAsync();

        // Both halves are required before an S3 or GCS secret can be created, so a key with no secret
        // is not "configured" — reporting it as ready would defer the failure to the first query.
        Assert.True(Profile(storage, "primary").CredentialsConfigured);
        Assert.False(Profile(storage, "half-configured").CredentialsConfigured);

        // A local path creates no secret at all, so there is nothing that could be missing.
        Assert.True(Profile(storage, "on-disk").CredentialsConfigured);
    }

    [Fact]
    public async Task Azure_reports_which_mode_is_configured_without_its_contents()
    {
        var storage = await ReadAsync();

        var connectionString = Profile(storage, "azure-string");
        Assert.Equal("connection-string", connectionString.AzureAuthentication);
        Assert.True(connectionString.CredentialsConfigured);

        var identity = Profile(storage, "azure-identity");
        Assert.Equal("credential-chain", identity.AzureAuthentication);
        Assert.True(identity.CredentialsConfigured);

        // Neither mode configured is a profile with nothing to authenticate with, not a third mode.
        var empty = Profile(storage, "azure-empty");
        Assert.Null(empty.AzureAuthentication);
        Assert.False(empty.CredentialsConfigured);
    }

    [Fact]
    public void Userinfo_written_into_an_endpoint_is_not_handed_back()
    {
        // DuckDB's ENDPOINT takes a bare host, so this is defence against a deployment that put a
        // credential somewhere the redaction rules did not expect one.
        var options = new LakehouseOptions();
        options.StorageProfiles["leaky"] = new ParquetStorageProfileOptions
        {
            Kind = ParquetStorageKind.S3,
            KeyId = S3Key,
            Secret = S3Secret,
            Endpoint = $"{S3Key}:{S3Secret}@minio.example.com:9000",
        };

        var storage = Read(options);

        var endpoint = Assert.Single(storage.Profiles).Endpoint;
        Assert.Equal("minio.example.com:9000", endpoint);
        Assert.DoesNotContain(S3Secret, endpoint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_deployment_that_names_no_default_profile_reports_none()
    {
        // Empty and unset have to look the same to the browser. A blank string would render as a
        // selected profile with no name, which is a worse answer than "none configured".
        var storage = Read(new LakehouseOptions { DefaultStorageProfile = "  " });

        Assert.Null(storage.DefaultStorageProfile);
        Assert.Empty(storage.Profiles);
    }

    private async Task<HttpResponseMessage> ResolveAsync(object request, string? bearer = null)
        => await Client(bearer ?? _instanceToken)
            .PostAsJsonAsync("/api/v1/system-settings/storage/resolve", request);

    [Fact]
    public async Task A_derived_placement_is_tenant_qualified_beneath_the_data_root()
    {
        using var response = await ResolveAsync(new { tenantSlug = "acme", catalogName = "analytics" });
        var resolved = await response.Content.ReadFromJsonAsync<ResolvedStoragePathDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(resolved);
        Assert.Equal("s3://company-lake/lakehold/acme/analytics", resolved.DataPath);
        Assert.Equal("S3", resolved.Kind);
        Assert.Equal("primary", resolved.StorageProfile);
        Assert.True(resolved.Derived);
    }

    [Fact]
    public async Task A_tenant_that_does_not_exist_yet_still_resolves()
    {
        // The first-run form previews a placement for a workspace it is about to create. Requiring
        // the row would make the preview unavailable in the one place it matters most.
        using var response = await ResolveAsync(
            new { tenantSlug = "not-created-yet", catalogName = "analytics" });
        var resolved = await response.Content.ReadFromJsonAsync<ResolvedStoragePathDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("s3://company-lake/lakehold/not-created-yet/analytics", resolved!.DataPath);
    }

    [Fact]
    public async Task An_explicit_placement_is_validated_rather_than_derived()
    {
        using var response = await ResolveAsync(new
        {
            tenantSlug = "acme",
            catalogName = "analytics",
            dataPath = "s3://customer-bucket/somewhere/else",
            storageProfile = "compatible",
        });
        var resolved = await response.Content.ReadFromJsonAsync<ResolvedStoragePathDto>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("s3://customer-bucket/somewhere/else", resolved!.DataPath);
        Assert.Equal("compatible", resolved.StorageProfile);

        // The operator's path is not rewritten, and the browser needs to know it will not move when
        // the catalog name is edited.
        Assert.False(resolved.Derived);
    }

    [Theory]
    // A name still being typed, and one that is never legal. Both reach the derivation helper, which
    // throws rather than returns — so without a guard this route answers 500 to ordinary keystrokes.
    [InlineData("acme", "", "catalog name")]
    [InlineData("acme", "order-items", "catalog name")]
    [InlineData("acme spaces", "analytics", "tenant slug")]
    public async Task An_underivable_name_is_a_400_rather_than_a_server_error(
        string tenantSlug,
        string catalogName,
        string expected)
    {
        using var response = await ResolveAsync(new { tenantSlug, catalogName });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            expected,
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://nope/here", null, "s3://")]
    [InlineData("s3://bucket/prefix", "azure-string", "requires S3")]
    [InlineData("s3://bucket/prefix", "missing-profile", "is not configured")]
    [InlineData("/mnt/lakehold/data", "primary", "must not select an object-storage profile")]
    public async Task An_unusable_placement_is_refused_with_the_reason(
        string dataPath,
        string? storageProfile,
        string expected)
    {
        using var response = await ResolveAsync(new
        {
            tenantSlug = "acme",
            catalogName = "analytics",
            dataPath,
            storageProfile,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            expected,
            await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolving_is_instance_scoped_like_the_rest_of_the_group()
    {
        using var response = await ResolveAsync(
            new { tenantSlug = "acme", catalogName = "analytics" },
            _tenantToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_preview_and_the_create_that_follows_it_agree()
    {
        // The reason the placement rules were extracted rather than copied. A preview that derived a
        // different path from the create it precedes is worse than no preview: the operator approves
        // one location and gets another, and nothing in either request looks wrong.
        using var response = await ResolveAsync(new { tenantSlug = "acme", catalogName = "ledger" });
        var previewed = await response.Content.ReadFromJsonAsync<ResolvedStoragePathDto>();

        await using var scope = _app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<LakehouseOptions>>();

        var created = await AdminEndpoints.CreateCatalogAsync(
            "acme",
            new CreateCatalogRequest("ledger"),
            context,
            options,
            TimeProvider.System,
            default);

        var catalog = Assert.IsType<Created<CatalogDto>>(((INestedHttpResult)created).Result).Value!;
        Assert.Equal(previewed!.DataPath, catalog.DataPath);
        Assert.Equal(previewed.Kind, catalog.StorageKind);
        Assert.Equal(previewed.StorageProfile, catalog.StorageProfile);
    }

    [Fact]
    public void The_documented_environment_keys_populate_what_this_endpoint_reads()
    {
        // Everything above configures the options directly, which cannot show that the
        // double-underscore form in docs/POSTGRES-AND-STORAGE.md is the form that fills them. A
        // documented key that binds to nothing leaves an operator configuring a profile this node
        // never sees, and the failure appears later as a catalog that will not attach.
        var keys = new Dictionary<string, string>
        {
            ["Lakehouse__DataRoot"] = "s3://company-lake/lakehold",
            ["Lakehouse__DefaultStorageProfile"] = "documented",
            ["Lakehouse__StorageProfiles__documented__Kind"] = "S3",
            ["Lakehouse__StorageProfiles__documented__KeyId"] = S3Key,
            ["Lakehouse__StorageProfiles__documented__Secret"] = S3Secret,
            ["Lakehouse__StorageProfiles__documented__Region"] = "eu-west-1",
            ["Lakehouse__StorageProfiles__documented__Endpoint"] = "minio.example.com:9000",
            ["Lakehouse__StorageProfiles__documented__UseSsl"] = "false",
            ["Lakehouse__StorageProfiles__documented__UrlStyle"] = "path",
        };

        foreach (var (key, value) in keys)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            var options = new LakehouseOptions();
            new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .Build()
                .GetSection(LakehouseOptions.SectionName)
                .Bind(options);

            var storage = Read(options);

            Assert.Equal("s3://company-lake/lakehold", storage.DataRoot);
            Assert.Equal("documented", storage.DefaultStorageProfile);

            // StorageProfiles is a get-only dictionary, which the binder has to populate in place
            // rather than assign. That it does is exactly what the documented per-profile keys rely on.
            var profile = Assert.Single(storage.Profiles);
            Assert.Equal("documented", profile.Name);
            Assert.Equal("S3", profile.Kind);
            Assert.Equal("eu-west-1", profile.Region);
            Assert.Equal("minio.example.com:9000", profile.Endpoint);
            Assert.False(profile.UseSsl);
            Assert.Equal("path", profile.UrlStyle);
            Assert.True(profile.CredentialsConfigured);
        }
        finally
        {
            foreach (var key in keys.Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    private static SystemStorageDto Read(LakehouseOptions options)
    {
        var result = SystemSettingsEndpoints.GetStorage(
            Microsoft.Extensions.Options.Options.Create(options));
        return Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.Ok<SystemStorageDto>>(result).Value!;
    }
}

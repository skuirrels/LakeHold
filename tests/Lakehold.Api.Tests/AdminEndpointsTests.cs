using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.Engine.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for provisioning and token management: a tenant and catalog can be created, a token is
///     shown once and never again, revocation is effective, and the reserved <c>admin</c> slug is
///     refused. These are the endpoints that make a fresh deployment usable at all.
/// </summary>
public sealed class AdminEndpointsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-admin", Guid.NewGuid().ToString("N"));
    private ControlPlaneContext _context = null!;
    private IOptions<Engine.Configuration.LakehouseOptions> _options = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = new DbContextOptionsBuilder<ControlPlaneContext>();
        builder.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}");
        _context = new ControlPlaneContext(builder.Options);
        await _context.Database.EnsureCreatedAsync();

        _options = Options.Create(new Engine.Configuration.LakehouseOptions
        {
            MetadataRoot = Path.Combine(_root, "catalogs"),
            DataRoot = Path.Combine(_root, "data"),
        });
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    private static IResult Unwrap(object union) => ((INestedHttpResult)union).Result;

    [Fact]
    public async Task A_tenant_named_admin_is_refused()
    {
        var result = await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("admin", "Reserved"), _context, TimeProvider.System, default);

        Assert.IsType<BadRequest<string>>(Unwrap(result));
        Assert.Equal(0, await _context.Tenants.CountAsync());
    }

    [Fact]
    public async Task A_tenant_and_catalog_can_be_provisioned()
    {
        var tenant = await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        Assert.IsType<Created<TenantDto>>(Unwrap(tenant));

        var catalog = await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest("analytics"), _context, _options, TimeProvider.System, default);
        var created = Assert.IsType<Created<CatalogDto>>(Unwrap(catalog));

        Assert.Equal("analytics", created.Value!.Name);

        var stored = await _context.Catalogs.SingleAsync();
        Assert.Equal(CatalogMetadataKind.Postgres, stored.MetadataKind);
        Assert.StartsWith("lh_dl_", stored.MetadataSource, StringComparison.Ordinal);
        Assert.StartsWith("lh_", stored.MetadataSchema, StringComparison.Ordinal);
        Assert.StartsWith("lh_pg_", stored.MetadataSecretName, StringComparison.Ordinal);
        Assert.Equal(
            CatalogStorageNamespace.Under(_options.Value.DataRoot, "acme", "analytics"),
            stored.DataPath);
        Assert.Equal(ParquetStorageKind.Local, stored.StorageKind);
    }

    [Fact]
    public async Task Same_named_catalogs_in_different_tenants_have_distinct_storage_namespaces()
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("beta", "Beta"), _context, TimeProvider.System, default);

        Assert.IsType<Created<CatalogDto>>(Unwrap(await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest("analytics"), _context, _options, TimeProvider.System, default)));
        Assert.IsType<Created<CatalogDto>>(Unwrap(await AdminEndpoints.CreateCatalogAsync(
            "beta", new CreateCatalogRequest("analytics"), _context, _options, TimeProvider.System, default)));

        var paths = await _context.Catalogs
            .OrderBy(catalog => catalog.Tenant!.Slug)
            .Select(catalog => catalog.DataPath)
            .ToListAsync();

        Assert.Equal(
            [
                CatalogStorageNamespace.Under(_options.Value.DataRoot, "acme", "analytics"),
                CatalogStorageNamespace.Under(_options.Value.DataRoot, "beta", "analytics"),
            ],
            paths);
        Assert.Equal(2, paths.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("main")]
    [InlineData("MAIN")]
    [InlineData("system")]
    [InlineData("temp")]
    public async Task A_DuckDB_reserved_catalog_name_is_refused_before_it_is_persisted(string name)
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);

        var result = await AdminEndpoints.CreateCatalogAsync(
            "acme", new CreateCatalogRequest(name), _context, _options, TimeProvider.System, default);

        var badRequest = Assert.IsType<BadRequest<string>>(Unwrap(result));
        Assert.Contains("reserved by DuckDB", badRequest.Value, StringComparison.Ordinal);
        Assert.Equal(0, await _context.Catalogs.CountAsync());
    }

    [Theory]
    [InlineData("s3://bucket/lake", ParquetStorageKind.S3)]
    [InlineData("gs://bucket/lake", ParquetStorageKind.Gcs)]
    [InlineData("gcs://bucket/lake", ParquetStorageKind.Gcs)]
    [InlineData("az://container/lake", ParquetStorageKind.Azure)]
    [InlineData("azure://container/lake", ParquetStorageKind.Azure)]
    [InlineData("abfss://container@account.dfs.core.windows.net/lake", ParquetStorageKind.Azure)]
    public async Task Remote_Parquet_backends_are_provisioned_through_named_profiles(
        string dataPath,
        ParquetStorageKind kind)
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        _options.Value.StorageProfiles["remote"] = new Engine.Configuration.ParquetStorageProfileOptions
        {
            Kind = kind,
        };

        var result = await AdminEndpoints.CreateCatalogAsync(
            "acme",
            new CreateCatalogRequest("analytics", dataPath, StorageProfile: "remote"),
            _context,
            _options,
            TimeProvider.System,
            default);

        Assert.IsType<Created<CatalogDto>>(Unwrap(result));
        var stored = await _context.Catalogs.SingleAsync();
        Assert.Equal(kind, stored.StorageKind);
        Assert.Equal("remote", stored.StorageProfile);
        Assert.StartsWith("lh_store_", stored.StorageSecretName, StringComparison.Ordinal);
        Assert.Equal(CatalogMetadataKind.Postgres, stored.MetadataKind);
    }

    [Fact]
    public async Task Remote_Parquet_is_refused_when_no_storage_profile_is_configured()
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);

        var result = await AdminEndpoints.CreateCatalogAsync(
            "acme",
            new CreateCatalogRequest("analytics", "s3://bucket/lake"),
            _context,
            _options,
            TimeProvider.System,
            default);

        var badRequest = Assert.IsType<BadRequest<string>>(Unwrap(result));
        Assert.Contains("storage profile", badRequest.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_context.Catalogs);
    }

    [Fact]
    public async Task A_token_is_shown_once_listed_without_its_secret_and_revocable()
    {
        await AdminEndpoints.CreateTenantAsync(new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);

        var create = await AdminEndpoints.CreateTokenAsync(
            "acme", new CreateTokenRequest("bi", ReadOnly: true), _context, TimeProvider.System, default);
        var minted = Assert.IsType<Created<CreatedTokenDto>>(Unwrap(create)).Value!;

        Assert.StartsWith("lkh_acme_", minted.Token, StringComparison.Ordinal);

        // The listing never carries the secret, and reports the metadata.
        var list = await AdminEndpoints.ListTokensAsync("acme", _context, default);
        var tokens = Assert.IsType<Ok<IReadOnlyList<ApiTokenDto>>>(Unwrap(list)).Value!;
        var only = Assert.Single(tokens);
        Assert.Equal("bi", only.Name);
        Assert.True(only.ReadOnly);
        Assert.Null(only.RevokedUtc);

        // The plaintext is not recoverable from anything the API stores.
        var row = await _context.ApiTokens.SingleAsync();
        Assert.NotEqual(minted.Token, row.SecretHash);

        // Revoking is effective and idempotent.
        Assert.IsType<NoContent>(Unwrap(await AdminEndpoints.RevokeTokenAsync("acme", minted.Id, _context, TimeProvider.System, default)));
        Assert.IsType<NoContent>(Unwrap(await AdminEndpoints.RevokeTokenAsync("acme", minted.Id, _context, TimeProvider.System, default)));

        var afterList = await AdminEndpoints.ListTokensAsync("acme", _context, default);
        var afterTokens = Assert.IsType<Ok<IReadOnlyList<ApiTokenDto>>>(Unwrap(afterList)).Value!;
        Assert.NotNull(Assert.Single(afterTokens).RevokedUtc);
    }

    [Fact]
    public async Task A_token_narrowed_to_an_unknown_catalog_is_refused()
    {
        await AdminEndpoints.CreateTenantAsync(new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);

        var result = await AdminEndpoints.CreateTokenAsync(
            "acme", new CreateTokenRequest("scoped", CatalogName: "ghost"), _context, TimeProvider.System, default);

        Assert.IsType<BadRequest<string>>(Unwrap(result));
        Assert.Equal(0, await _context.ApiTokens.CountAsync());
    }

    [Fact]
    public async Task A_public_token_request_persists_its_role_catalog_and_expiry()
    {
        await AdminEndpoints.CreateTenantAsync(
            new CreateTenantRequest("acme", "Acme"), _context, TimeProvider.System, default);
        await AdminEndpoints.CreateCatalogAsync(
            "acme",
            new CreateCatalogRequest("analytics"),
            _context,
            _options,
            TimeProvider.System,
            default);
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await AdminEndpoints.CreateTokenAsync(
            "acme",
            new CreateTokenRequest(
                "codex-agent",
                ReadOnly: true,
                CatalogName: "analytics",
                ExpiresUtc: expiry,
                Role: "editor"),
            _context,
            TimeProvider.System,
            default);

        Assert.IsType<Created<CreatedTokenDto>>(Unwrap(result));
        var stored = await _context.ApiTokens.SingleAsync();
        Assert.Equal("codex-agent", stored.Name);
        Assert.Equal(TokenRole.Editor, stored.Role);
        Assert.True(stored.ReadOnly);
        Assert.Equal("analytics", stored.CatalogName);
        Assert.Equal(expiry, stored.ExpiresUtc);
    }
}

using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
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

        // The derived local metadata directory was created so the first attach will succeed.
        Assert.True(Directory.Exists(_options.Value.MetadataRoot));

        var stored = await _context.Catalogs.SingleAsync();
        Assert.Equal(Path.Combine(_options.Value.MetadataRoot, "analytics.ducklake"), stored.MetadataSource);
        Assert.Equal(Path.Combine(_options.Value.DataRoot, "analytics"), stored.DataPath);
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
}

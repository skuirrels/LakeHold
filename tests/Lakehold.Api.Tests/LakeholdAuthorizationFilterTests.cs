using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     The step-2 gate, exercised end to end through the real filter: a token is resolved against a
///     real control plane, and the route is validated against it. Cross-tenant and cross-catalog
///     reach is refused; revoked, expired, and malformed tokens are refused; an instance token cannot
///     reach a data route; and a token-less request still works while enforcement is not required.
/// </summary>
public sealed class LakeholdAuthorizationFilterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-auth", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    private string _fullToken = null!;
    private string _analyticsOnlyToken = null!;
    private string _revokedToken = null!;
    private string _expiredToken = null!;
    private string _instanceToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _services = BuildServices(requireAuthentication: false);

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        var other = new Tenant { Slug = "other", DisplayName = "Other", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.AddRange(demo, other);
        await context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        _fullToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "full", now));
        _analyticsOnlyToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "analytics", now, catalogName: "analytics"));

        var revoked = ApiTokenFactory.Issue(TokenScope.Tenant, demo, "revoked", now);
        revoked.Record.RevokedUtc = now;
        _revokedToken = Persist(context, revoked);

        _expiredToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "expired", now, expiresUtc: now.AddMinutes(-1)));
        _instanceToken = Persist(context, ApiTokenFactory.Issue(TokenScope.Instance, tenant: null, "admin", now));

        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup failing must not fail the run.
        }
    }

    [Fact]
    public async Task A_full_token_reaches_its_own_tenant()
    {
        var (status, passed) = await RunAsync(_services, _fullToken, "demo", "analytics");

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task A_token_cannot_reach_another_tenant()
    {
        var (status, passed) = await RunAsync(_services, _fullToken, "other", "otherlake");

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Fact]
    public async Task A_catalog_scoped_token_reaches_only_its_catalog()
    {
        var allowed = await RunAsync(_services, _analyticsOnlyToken, "demo", "analytics");
        var refused = await RunAsync(_services, _analyticsOnlyToken, "demo", "events");

        Assert.True(allowed.Passed);
        Assert.Equal(StatusCodes.Status404NotFound, refused.Status);
    }

    [Fact]
    public async Task An_instance_token_cannot_reach_a_data_route()
    {
        var (status, passed) = await RunAsync(_services, _instanceToken, "demo", "analytics");

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Theory]
    [InlineData("lkh_demo_this-is-not-the-real-secret")]
    [InlineData("not-a-lakehold-token")]
    public async Task A_malformed_or_unknown_token_is_refused(string bearer)
    {
        var (status, passed) = await RunAsync(_services, bearer, "demo", "analytics");

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var (status, _) = await RunAsync(_services, _revokedToken, "demo", "analytics");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        var (status, _) = await RunAsync(_services, _expiredToken, "demo", "analytics");
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task No_token_is_allowed_while_authentication_is_not_required()
    {
        var (status, passed) = await RunAsync(_services, bearer: null, "demo", "analytics");

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task No_token_is_refused_when_authentication_is_required()
    {
        await using var services = BuildServices(requireAuthentication: true);

        var (status, passed) = await RunAsync(services, bearer: null, "demo", "analytics");

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task An_instance_token_reaches_a_provisioning_route()
    {
        var (status, passed) = await RunAsync(
            _services, _instanceToken, tenant: null, catalog: null, RouteCapability.Instance);

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task A_tenant_token_cannot_reach_a_provisioning_route()
    {
        var (status, passed) = await RunAsync(
            _services, _fullToken, tenant: null, catalog: null, RouteCapability.Instance);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    [Fact]
    public async Task A_full_tenant_token_may_administer_its_own_tokens()
    {
        var (status, passed) = await RunAsync(
            _services, _fullToken, "demo", catalog: null, RouteCapability.TenantAdmin);

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task An_instance_token_may_administer_any_tenants_tokens()
    {
        var (status, passed) = await RunAsync(
            _services, _instanceToken, "demo", catalog: null, RouteCapability.TenantAdmin);

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
    }

    [Fact]
    public async Task A_least_privilege_tenant_token_cannot_administer_tokens()
    {
        // A catalog-narrowed (and read-only) credential is least privilege by design; it must not be
        // able to mint a broader one.
        var (status, passed) = await RunAsync(
            _services, _analyticsOnlyToken, "demo", catalog: null, RouteCapability.TenantAdmin);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
    }

    private ServiceProvider BuildServices(bool requireAuthentication)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneContext>(o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        services.AddScoped<ApiTokenAuthenticator>();
        services.AddSingleton(TimeProvider.System);
        services.Configure<LakeholdAuthOptions>(o => o.RequireAuthentication = requireAuthentication);
        return services.BuildServiceProvider();
    }

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private static async Task<(int Status, bool Passed)> RunAsync(
        IServiceProvider services, string? bearer, string? tenant, string? catalog, RouteCapability? capability = null)
    {
        using var scope = services.CreateScope();

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        if (tenant is not null)
        {
            http.Request.RouteValues["tenantSlug"] = tenant;
        }

        if (catalog is not null)
        {
            http.Request.RouteValues["catalogName"] = catalog;
        }

        if (capability is { } cap)
        {
            http.SetEndpoint(new Endpoint(
                requestDelegate: null,
                new EndpointMetadataCollection(new RouteCapabilityMetadata(cap)),
                displayName: "test"));
        }

        if (bearer is not null)
        {
            http.Request.Headers.Authorization = "Bearer " + bearer;
        }

        var filter = new LakeholdAuthorizationFilter();
        var invocation = EndpointFilterInvocationContext.Create(http);

        var passed = false;
        var result = await filter.InvokeAsync(invocation, _ =>
        {
            passed = true;
            return ValueTask.FromResult<object?>(Results.Ok("ok"));
        });

        if (result is IResult typed)
        {
            await typed.ExecuteAsync(http);
        }

        return (http.Response.StatusCode, passed);
    }
}

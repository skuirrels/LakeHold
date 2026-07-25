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
///     Cover for roles (phase 4): maintenance, restore, and eject are owner operations; querying is a
///     reader's; and a reader is read-only by construction rather than by a separate flag a caller
///     could forget. Exercised through the real filter against a real control plane.
/// </summary>
public sealed class TokenRoleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-roles", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    private string _owner = null!;
    private string _editor = null!;
    private string _reader = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddDbContext<ControlPlaneContext>(o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        collection.AddScoped<ApiTokenAuthenticator>();
        collection.AddSingleton(TimeProvider.System);
        collection.Configure<LakeholdAuthOptions>(o => o.RequireAuthentication = false);
        collection.Configure<LakeholdOidcOptions>(_ => { });
        _services = collection.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.Add(demo);
        await context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        _owner = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "owner", now, role: TokenRole.Owner));
        _editor = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "editor", now, role: TokenRole.Editor));
        _reader = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "reader", now, role: TokenRole.Reader));
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
    public void A_reader_token_is_read_only_by_construction()
    {
        var issued = ApiTokenFactory.Issue(
            TokenScope.Tenant,
            new Tenant { Id = 1, Slug = "demo", DisplayName = "Demo" },
            "reader",
            DateTimeOffset.UtcNow,
            role: TokenRole.Reader);

        // Asking for a reader without also passing readOnly must not produce a writable credential.
        Assert.True(issued.Record.ReadOnly);
        Assert.Equal(TokenRole.Reader, issued.Record.Role);
    }

    /// <summary>
    ///     Issuing without naming a role must produce the least privileged credential, not the most.
    ///     The default was <see cref="TokenRole.Owner"/> at first, so that tokens minted before roles
    ///     existed kept working; that made the most obvious request — a name and nothing else — mint
    ///     full privilege. Old rows are still owners by way of the column's zero value, which is a
    ///     separate question from what a fresh token should be.
    /// </summary>
    [Fact]
    public void Issuing_without_a_role_produces_a_reader()
    {
        var issued = ApiTokenFactory.Issue(
            TokenScope.Tenant,
            new Tenant { Id = 1, Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UnixEpoch },
            "unspecified",
            DateTimeOffset.UtcNow);

        Assert.Equal(TokenRole.Reader, issued.Record.Role);

        // And a reader is read-only by attachment, so the default cannot write either.
        Assert.True(issued.Record.ReadOnly);
    }

    /// <summary>
    ///     An unrecognised role name resolves to a reader rather than to an owner. A typo should cost
    ///     the caller a 403 the first time they write, never hand them everything.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ownr")]
    [InlineData("superuser")]
    public void An_absent_or_unrecognised_role_resolves_to_reader(string? value)
        => Assert.Equal(TokenRole.Reader, TokenRoleParser.Parse(value, TokenRole.Reader));

    [Fact]
    public void An_instance_token_is_always_an_owner()
    {
        var issued = ApiTokenFactory.Issue(
            TokenScope.Instance, tenant: null, "admin", DateTimeOffset.UtcNow, role: TokenRole.Reader);

        Assert.Equal(TokenRole.Owner, issued.Record.Role);
    }

    [Fact]
    public async Task Every_role_may_query()
    {
        foreach (var token in new[] { _owner, _editor, _reader })
        {
            var (status, passed) = await RunAsync(token, Capability.TenantData);
            Assert.True(passed);
            Assert.Equal(StatusCodes.Status200OK, status);
        }
    }

    [Fact]
    public async Task Only_an_owner_may_run_maintenance_or_eject()
    {
        var owner = await RunAsync(_owner, Capability.TenantOwner);
        Assert.True(owner.Passed);

        foreach (var token in new[] { _editor, _reader })
        {
            var (status, passed) = await RunAsync(token, Capability.TenantOwner);
            Assert.False(passed);
            Assert.Equal(StatusCodes.Status403Forbidden, status);
        }
    }

    [Fact]
    public async Task Only_an_owner_may_manage_tokens()
    {
        var owner = await RunAsync(_owner, Capability.TenantAdmin);
        Assert.True(owner.Passed);

        foreach (var token in new[] { _editor, _reader })
        {
            var (status, passed) = await RunAsync(token, Capability.TenantAdmin);
            Assert.False(passed);
            Assert.Equal(StatusCodes.Status403Forbidden, status);
        }
    }

    [Fact]
    public async Task An_owner_from_another_tenant_is_still_a_404_not_a_403()
    {
        // Subject is checked before capability, so an unreachable tenant never confirms it exists.
        var (status, passed) = await RunAsync(_owner, Capability.TenantOwner, tenant: "other");

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private async Task<(int Status, bool Passed)> RunAsync(
        string bearer, Capability capability, string tenant = "demo")
    {
        using var scope = _services.CreateScope();

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        http.Request.RouteValues["tenantSlug"] = tenant;
        http.Request.RouteValues["catalogName"] = "analytics";
        http.Request.Headers.Authorization = "Bearer " + bearer;
        http.SetEndpoint(new Endpoint(
            requestDelegate: null,
            new EndpointMetadataCollection(new RouteCapabilityMetadata(capability)),
            displayName: "test"));

        var passed = false;
        var result = await new LakeholdAuthorizationFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(http),
            _ =>
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

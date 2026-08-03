using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Mcp;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     The divergence that makes the MCP surface safe to expose: it demands a credential
///     unconditionally, where every other surface still falls back to trusting the route while
///     <see cref="LakeholdAuthOptions.RequireAuthentication"/> is false (invariant 21).
/// </summary>
public sealed class McpAuthenticationFilterTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lakehold-mcp-auth", Guid.NewGuid().ToString("N"));
    private ServiceProvider _services = null!;

    private string _token = null!;
    private string _revokedToken = null!;
    private string _expiredToken = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ControlPlaneContext>(o => o.UseDuckDB($"Data Source={Path.Combine(_root, "cp.duckdb")}"));
        services.AddScoped<ApiTokenAuthenticator>();
        services.AddScoped<McpRuntimeSettingsStore>();
        services.AddSingleton(TimeProvider.System);
        services.Configure<McpOptions>(options => options.Enabled = true);
        services.Configure<LakeholdOidcOptions>(_ => { });
        _services = services.BuildServiceProvider();

        await using var scope = _services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ControlPlaneContext>();
        await context.Database.EnsureCreatedAsync();

        var demo = new Tenant { Slug = "demo", DisplayName = "Demo", CreatedUtc = DateTimeOffset.UtcNow };
        context.Tenants.Add(demo);
        await context.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        _token = Persist(context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "agent", now, role: TokenRole.Owner));

        var revoked = ApiTokenFactory.Issue(TokenScope.Tenant, demo, "revoked", now);
        revoked.Record.RevokedUtc = now;
        _revokedToken = Persist(context, revoked);

        _expiredToken = Persist(
            context, ApiTokenFactory.Issue(TokenScope.Tenant, demo, "expired", now, expiresUtc: now.AddMinutes(-1)));

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
    public async Task No_credential_is_refused_even_though_authentication_is_not_required()
    {
        // The same request against an HTTP data route is allowed today. This is invariant 21, and it
        // is the one behaviour that must not regress when the surrounding default changes.
        var (status, passed, _) = await RunAsync(bearer: null);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task A_valid_token_resolves_and_is_stashed_for_the_tools()
    {
        var (status, passed, principal) = await RunAsync(_token);

        Assert.True(passed);
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.NotNull(principal);
        Assert.Equal("demo", principal.TenantSlug);
    }

    [Fact]
    public async Task A_runtime_disable_is_applied_without_rebuilding_the_endpoint()
    {
        await using (var scope = _services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: false,
                    allowWrites: false,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 0,
                    CancellationToken.None);
        }

        try
        {
            var (status, passed, _) = await RunAsync(_token);

            Assert.False(passed);
            Assert.Equal(StatusCodes.Status404NotFound, status);
        }
        finally
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>()
                .SaveAsync(
                    enabled: true,
                    allowWrites: false,
                    maxRowsPerResult: 200,
                    publicBaseUrl: null,
                    expectedVersion: 1,
                    CancellationToken.None);
        }
    }

    [Fact]
    public async Task Public_base_url_length_is_validated_before_persistence()
    {
        const string prefix = "https://lakehold.example.com/";
        var longestValid = prefix + new string(
            'a',
            SystemSettings.McpPublicBaseUrlMaxLength - prefix.Length);

        await using var scope = _services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<McpRuntimeSettingsStore>();
        var saved = await store.SaveAsync(
            enabled: true,
            allowWrites: false,
            maxRowsPerResult: 200,
            publicBaseUrl: longestValid,
            expectedVersion: 0,
            CancellationToken.None);

        Assert.Equal(longestValid, saved.PublicBaseUrl);

        var exception = await Assert.ThrowsAsync<SystemSettingsValidationException>(() =>
            store.SaveAsync(
                enabled: true,
                allowWrites: false,
                maxRowsPerResult: 200,
                publicBaseUrl: longestValid + "a",
                expectedVersion: saved.Version,
                CancellationToken.None));
        Assert.Contains("2048", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lkh_demo_this-is-not-the-real-secret")]
    [InlineData("not-a-lakehold-token")]
    public async Task A_malformed_or_unknown_credential_is_refused(string bearer)
    {
        var (status, passed, _) = await RunAsync(bearer);

        Assert.False(passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task A_revoked_credential_is_refused()
    {
        var (status, _, _) = await RunAsync(_revokedToken);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task An_expired_credential_is_refused()
    {
        var (status, _, _) = await RunAsync(_expiredToken);
        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task A_refusal_challenges_without_naming_the_reason()
    {
        // One opaque refusal for missing, malformed, unknown, revoked, and expired alike: which of
        // those it was is information a caller has no right to.
        var (_, _, _, headers) = await RunDetailedAsync(_revokedToken);
        Assert.Equal("Bearer", headers.WWWAuthenticate.ToString());
    }

    private static string Persist(ControlPlaneContext context, IssuedToken issued)
    {
        context.ApiTokens.Add(issued.Record);
        return issued.Plaintext;
    }

    private async Task<(int Status, bool Passed, ILakeholdPrincipal? Principal)> RunAsync(string? bearer)
    {
        var (status, passed, principal, _) = await RunDetailedAsync(bearer);
        return (status, passed, principal);
    }

    private async Task<(int Status, bool Passed, ILakeholdPrincipal? Principal, IHeaderDictionary Headers)>
        RunDetailedAsync(string? bearer)
    {
        using var scope = _services.CreateScope();

        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Response.Body = new MemoryStream();
        if (bearer is not null)
        {
            http.Request.Headers.Authorization = "Bearer " + bearer;
        }

        var filter = new McpAuthenticationFilter();
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

        var principal = http.Items.TryGetValue(LakeholdAuthorizationFilter.PrincipalItemKey, out var value)
            ? value as ILakeholdPrincipal
            : null;

        return (http.Response.StatusCode, passed, principal, http.Response.Headers);
    }
}

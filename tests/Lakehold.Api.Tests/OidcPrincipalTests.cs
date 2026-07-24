using System.Security.Claims;
using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Model;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for mapping a validated OIDC identity onto the same principal a token produces: the
///     tenant comes from a configurable claim, a role narrows capability, and an identity that names
///     no tenant resolves to nothing rather than to a tenant-less principal downstream code would have
///     to special-case.
/// </summary>
public sealed class OidcPrincipalTests
{
    private static readonly LakeholdOidcOptions Options = new() { Authority = "https://idp.test" };

    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(c => new Claim(c.Type, c.Value))], "TestJwt"));

    [Fact]
    public void A_tenant_claim_becomes_a_tenant_principal()
    {
        var principal = OidcPrincipal.TryResolve(User(("tenant", "demo"), ("role", "owner")), Options);

        Assert.NotNull(principal);
        Assert.True(principal.IsAuthenticated);
        Assert.Equal(TokenScope.Tenant, principal.Scope);
        Assert.Equal("demo", principal.TenantSlug);
        Assert.Equal(TokenRole.Owner, principal.Role);

        // A human authenticates as the whole tenant, and carries no token id: the audit trail records
        // an API token only when one was used.
        Assert.Null(principal.CatalogName);
        Assert.Null(principal.TokenId);
    }

    [Fact]
    public void A_reader_claim_is_read_only()
    {
        var principal = OidcPrincipal.TryResolve(User(("tenant", "demo"), ("role", "reader")), Options);

        Assert.NotNull(principal);
        Assert.True(principal.IsReadOnly);
        Assert.Equal(TokenRole.Reader, principal.Role);
    }

    [Fact]
    public void No_role_claim_defaults_to_editor_rather_than_owner()
    {
        // A human with no role can query and write, but not run destructive maintenance or eject.
        var principal = OidcPrincipal.TryResolve(User(("tenant", "demo")), Options);

        Assert.NotNull(principal);
        Assert.Equal(TokenRole.Editor, principal.Role);
        Assert.False(principal.IsReadOnly);
    }

    [Fact]
    public void An_identity_naming_no_tenant_resolves_to_nothing()
    {
        Assert.Null(OidcPrincipal.TryResolve(User(("sub", "user-1")), Options));
    }

    [Fact]
    public void An_unauthenticated_or_absent_identity_resolves_to_nothing()
    {
        Assert.Null(OidcPrincipal.TryResolve(null, Options));

        // An identity with no authentication type is not authenticated, whatever claims it carries.
        Assert.Null(OidcPrincipal.TryResolve(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", "demo")])), Options));
    }

    [Fact]
    public void The_claim_names_are_configurable()
    {
        var options = new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            TenantClaim = "https://lakehold.example/tenant",
            RoleClaim = "https://lakehold.example/role",
        };

        var principal = OidcPrincipal.TryResolve(
            User(("https://lakehold.example/tenant", "acme"), ("https://lakehold.example/role", "readonly")),
            options);

        Assert.NotNull(principal);
        Assert.Equal("acme", principal.TenantSlug);
        Assert.Equal(TokenRole.Reader, principal.Role);
    }
}

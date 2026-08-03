using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the one check every tenant-scoped path shares: a route is reachable only when it
///     matches the credential, and a mismatch is a 404 rather than a 403.
/// </summary>
public sealed class TenantAccessPolicyTests
{
    private static LakeholdPrincipal Tenant(string slug, string? catalog = null) =>
        new(TokenScope.Tenant, TenantId: 1, TenantSlug: slug,
            CatalogName: catalog, IsReadOnly: false, TokenId: 1);

    private static LakeholdPrincipal Instance() =>
        new(TokenScope.Instance, TenantId: null, TenantSlug: null,
            CatalogName: null, IsReadOnly: false, TokenId: 2);

    [Fact]
    public void A_tenant_token_reaches_its_own_tenant()
    {
        Assert.Equal(AccessDecision.Allow, TenantAccessPolicy.Evaluate(Tenant("demo"), "demo", "analytics"));
        Assert.Equal(AccessDecision.Allow, TenantAccessPolicy.Evaluate(Tenant("demo"), "demo", null));
    }

    [Fact]
    public void A_tenant_token_cannot_reach_another_tenant()
    {
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(Tenant("demo"), "other", "secret"));
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(Tenant("demo"), "other", null));
    }

    [Fact]
    public void A_catalog_scoped_token_reaches_only_its_catalog()
    {
        var scoped = Tenant("demo", catalog: "analytics");

        Assert.Equal(AccessDecision.Allow, TenantAccessPolicy.Evaluate(scoped, "demo", "analytics"));
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(scoped, "demo", "events"));
    }

    [Fact]
    public void An_instance_token_matches_no_data_route()
    {
        // It names no tenant, so the tenant check refuses every tenant-scoped path — no special case.
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(Instance(), "demo", "analytics"));
    }

    [Fact]
    public void A_demo_visitor_reaches_only_its_published_catalog()
    {
        // Replaces a test asserting that an unauthenticated caller was allowed any route. That
        // principal no longer exists; the demo visitor is the only credential-less identity left,
        // and unlike the old one it is bound to exactly one tenant and catalog.
        var demo = LakeholdPrincipal.Demo("demo", "analytics");

        Assert.Equal(AccessDecision.Allow, TenantAccessPolicy.Evaluate(demo, "demo", "analytics"));
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(demo, "demo", "private"));
        Assert.Equal(AccessDecision.NotFound, TenantAccessPolicy.Evaluate(demo, "other", "analytics"));
    }
}

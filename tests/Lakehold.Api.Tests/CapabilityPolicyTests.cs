using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Cover for the capability rules themselves, exercised directly rather than through a transport.
///     <see cref="LakeholdAuthorizationFilterTests"/> proves the HTTP surface maps them onto the right
///     status codes; these prove the rules, so a second transport can depend on them without
///     re-deriving what a refusal means or in what order the checks happen.
/// </summary>
public sealed class CapabilityPolicyTests
{
    private static LakeholdPrincipal Tenant(
        string slug,
        string? catalog = null,
        bool readOnly = false,
        TokenRole role = TokenRole.Owner) =>
        new(IsAuthenticated: true, TokenScope.Tenant, TenantId: 1, TenantSlug: slug,
            CatalogName: catalog, IsReadOnly: readOnly, TokenId: 1, Role: role);

    private static LakeholdPrincipal Instance() =>
        new(IsAuthenticated: true, TokenScope.Instance, TenantId: null, TenantSlug: null,
            CatalogName: null, IsReadOnly: false, TokenId: 2);

    private static CapabilityOutcome Outcome(
        ILakeholdPrincipal principal, RouteCapability capability, string? tenant = "demo", string? catalog = null) =>
        CapabilityPolicy.Evaluate(principal, capability, tenant, catalog).Outcome;

    [Fact]
    public void A_default_decision_refuses()
    {
        // The zero value must not be Allowed: a decision type that fails open is a decision type that
        // eventually fails open in production.
        Assert.Equal(CapabilityOutcome.NotFound, default(CapabilityDecision).Outcome);
    }

    [Fact]
    public void A_refusal_that_hides_something_explains_nothing()
    {
        // NotFound exists to avoid confirming the tenant, so it must not carry a reason that does.
        var decision = CapabilityPolicy.Evaluate(Tenant("demo"), RouteCapability.TenantData, "other", null);

        Assert.Equal(CapabilityOutcome.NotFound, decision.Outcome);
        Assert.Null(decision.Detail);
    }

    [Fact]
    public void A_forbidden_decision_says_why()
    {
        var decision = CapabilityPolicy.Evaluate(Tenant("demo"), RouteCapability.Instance, tenant: null, catalog: null);

        Assert.Equal(CapabilityOutcome.Forbidden, decision.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(decision.Detail));
    }

    [Theory]
    [InlineData(RouteCapability.TenantData)]
    [InlineData(RouteCapability.TenantOwner)]
    [InlineData(RouteCapability.TenantAdmin)]
    [InlineData(RouteCapability.Instance)]
    [InlineData(RouteCapability.Listing)]
    public void An_unauthenticated_caller_trusts_the_route_for_every_capability(RouteCapability capability)
    {
        // The transitional open path. A transport that must not offer it — an agent-reachable one —
        // refuses the credential-less caller before reaching here rather than changing this answer.
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(LakeholdPrincipal.Legacy, capability));
    }

    [Fact]
    public void Subject_is_checked_before_capability()
    {
        // The ordering that keeps invariant 19 true: a reader reaching an unreachable tenant must be
        // told 404, not 403. A 403 would confirm the tenant exists — and a policy that checked the
        // role first would produce exactly that.
        var reader = Tenant("demo", role: TokenRole.Reader);

        Assert.Equal(CapabilityOutcome.NotFound, Outcome(reader, RouteCapability.TenantOwner, tenant: "other"));
        Assert.Equal(CapabilityOutcome.Forbidden, Outcome(reader, RouteCapability.TenantOwner, tenant: "demo"));
    }

    [Fact]
    public void Owner_capability_requires_the_owner_role()
    {
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Tenant("demo"), RouteCapability.TenantOwner));
        Assert.Equal(
            CapabilityOutcome.Forbidden,
            Outcome(Tenant("demo", role: TokenRole.Editor), RouteCapability.TenantOwner));
        Assert.Equal(
            CapabilityOutcome.Forbidden,
            Outcome(Tenant("demo", role: TokenRole.Reader), RouteCapability.TenantOwner));
    }

    [Fact]
    public void Instance_capability_admits_only_an_instance_credential()
    {
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Instance(), RouteCapability.Instance, tenant: null));
        Assert.Equal(CapabilityOutcome.Forbidden, Outcome(Tenant("demo"), RouteCapability.Instance, tenant: null));
    }

    [Fact]
    public void An_instance_credential_reaches_no_tenant_data()
    {
        Assert.Equal(CapabilityOutcome.NotFound, Outcome(Instance(), RouteCapability.TenantData));
        Assert.Equal(CapabilityOutcome.NotFound, Outcome(Instance(), RouteCapability.TenantOwner));
    }

    [Fact]
    public void A_least_privilege_credential_cannot_mint_a_broader_one()
    {
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Tenant("demo"), RouteCapability.TenantAdmin));
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Instance(), RouteCapability.TenantAdmin));

        Assert.Equal(
            CapabilityOutcome.Forbidden,
            Outcome(Tenant("demo", catalog: "analytics"), RouteCapability.TenantAdmin));
        Assert.Equal(
            CapabilityOutcome.Forbidden,
            Outcome(Tenant("demo", readOnly: true), RouteCapability.TenantAdmin));
        Assert.Equal(
            CapabilityOutcome.Forbidden,
            Outcome(Tenant("demo", role: TokenRole.Editor), RouteCapability.TenantAdmin));

        // Another tenant's tokens are not merely forbidden — that tenant is not disclosed at all.
        Assert.Equal(CapabilityOutcome.NotFound, Outcome(Tenant("demo"), RouteCapability.TenantAdmin, tenant: "other"));
    }

    [Fact]
    public void Listing_admits_every_principal_because_the_handler_scopes_the_result()
    {
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Instance(), RouteCapability.Listing, tenant: null));
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Tenant("demo"), RouteCapability.Listing));
        Assert.Equal(CapabilityOutcome.Allowed, Outcome(Tenant("demo", readOnly: true), RouteCapability.Listing));
    }

    [Fact]
    public void A_catalog_narrowed_credential_reaches_only_its_catalog()
    {
        var scoped = Tenant("demo", catalog: "analytics");

        Assert.Equal(CapabilityOutcome.Allowed, Outcome(scoped, RouteCapability.TenantData, catalog: "analytics"));
        Assert.Equal(CapabilityOutcome.NotFound, Outcome(scoped, RouteCapability.TenantData, catalog: "events"));
    }
}

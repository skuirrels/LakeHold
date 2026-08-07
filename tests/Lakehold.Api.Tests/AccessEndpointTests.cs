using Lakehold.Api.Auth;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     What <c>/access</c> tells the Workbench about itself.
///
///     The administration navigation is built from this response, so a flag that overstates a
///     credential's capability is a menu item leading to a page the API refuses. These assert that
///     <c>TenantAdmin</c> is the capability policy's answer and not a guess made from the role: the
///     first two refused cases below hold <see cref="TokenRole.Owner"/> and are refused anyway.
/// </summary>
public sealed class AccessEndpointTests
{
    private static AccessDto Access(ILakeholdPrincipal principal)
    {
        var http = new DefaultHttpContext();
        http.Items[LakeholdAuthorizationFilter.PrincipalItemKey] = principal;
        return LakehouseEndpoints.GetAccess(http).Value!;
    }

    private static LakeholdPrincipal Owner(
        bool readOnly = false,
        string? catalog = null,
        TokenRole role = TokenRole.Owner,
        bool demo = false) =>
        new(TokenScope.Tenant, TenantId: 1, TenantSlug: "demo",
            CatalogName: catalog, IsReadOnly: readOnly, TokenId: 1, Role: role, IsDemo: demo);

    [Fact]
    public void A_full_owner_credential_administers_its_own_workspace()
    {
        var access = Access(Owner());

        Assert.True(access.TenantAdmin);
        Assert.False(access.SystemAdmin);
        Assert.Equal("authenticated", access.Mode);
    }

    [Fact]
    public void An_instance_credential_administers_the_instance_and_any_workspace()
    {
        var access = Access(new LakeholdPrincipal(
            TokenScope.Instance, TenantId: null, TenantSlug: null,
            CatalogName: null, IsReadOnly: false, TokenId: 2));

        Assert.True(access.SystemAdmin);
        Assert.True(access.TenantAdmin);
    }

    [Theory]
    [InlineData(true, null, TokenRole.Owner, false)]
    [InlineData(false, "analytics", TokenRole.Owner, false)]
    [InlineData(false, null, TokenRole.Editor, false)]
    [InlineData(true, "analytics", TokenRole.Reader, true)]
    public void A_least_privilege_credential_administers_nothing(
        bool readOnly,
        string? catalog,
        TokenRole role,
        bool demo)
    {
        // A read-only or catalog-narrowed token is least privilege by design: it must not be able to
        // mint a broader one, and so must not be offered the surface that mints one.
        Assert.False(Access(Owner(readOnly, catalog, role, demo)).TenantAdmin);
    }
}

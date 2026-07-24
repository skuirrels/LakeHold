using System.Security.Claims;
using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.Auth;

/// <summary>
///     Maps a validated OIDC identity to an <see cref="ILakeholdPrincipal"/>, so a human logging into
///     the workbench and a machine bearing a token both flow through the same downstream code.
/// </summary>
/// <remarks>
///     The JWT itself is validated by ASP.NET Core's bearer middleware before this runs; here we only
///     read the claims that name the tenant and role. A JWT that authenticates but names no tenant
///     resolves to nothing rather than to a tenant-less principal — an identity the product cannot map
///     to a catalog is not one it can serve, and guessing would be the wrong kind of helpful.
/// </remarks>
public static class OidcPrincipal
{
    /// <summary>
    ///     Resolves <paramref name="user"/> to a tenant principal, or null when it is unauthenticated
    ///     or names no tenant.
    /// </summary>
    public static LakeholdPrincipal? TryResolve(ClaimsPrincipal? user, LakeholdOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var tenantSlug = user.FindFirstValue(options.TenantClaim);
        if (string.IsNullOrEmpty(tenantSlug))
        {
            return null;
        }

        // A human with no role claim can query and write their tenant but not run destructive
        // maintenance or eject — those stay owner-only. Operators grant more by emitting the claim.
        var role = TokenRoleParser.Parse(user.FindFirstValue(options.RoleClaim), TokenRole.Editor);

        // Humans authenticate as the whole tenant, not narrowed to a catalog; capability comes from
        // the role. TenantId is not known from the token alone and is left null — enforcement keys on
        // the slug, and the audit trail records the token id only for API tokens.
        return new LakeholdPrincipal(
            IsAuthenticated: true,
            Scope: TokenScope.Tenant,
            TenantId: null,
            TenantSlug: tenantSlug,
            CatalogName: null,
            IsReadOnly: role == TokenRole.Reader,
            TokenId: null,
            Role: role);
    }
}

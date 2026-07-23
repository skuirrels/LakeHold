namespace Lakehold.ControlPlane.Security;

/// <summary>The outcome of checking a principal against a route.</summary>
public enum AccessDecision
{
    /// <summary>The principal may reach the route.</summary>
    Allow,

    /// <summary>The route names a tenant or catalog this principal cannot reach. Reported as a 404.</summary>
    NotFound,
}

/// <summary>
///     Validates the tenant and catalog a request names against the resolved principal. This is the
///     one check every tenant-scoped data path shares: a route that does not match the credential is
///     refused, so the route segment is never trusted on its own.
/// </summary>
public static class TenantAccessPolicy
{
    /// <summary>
    ///     Decides whether <paramref name="principal"/> may reach the given route tenant and catalog.
    /// </summary>
    /// <remarks>
    ///     A mismatch is a 404 rather than a 403 on purpose: a 403 confirms the tenant or catalog
    ///     exists, which is itself information the caller has no right to. An instance-scoped principal
    ///     names no tenant, so it matches no data route and is refused by the same rule — no special
    ///     case. An unauthenticated (route-trusting) caller is allowed, which keeps the product working
    ///     for token-less clients until authentication is required.
    /// </remarks>
    public static AccessDecision Evaluate(ILakeholdPrincipal principal, string? routeTenant, string? routeCatalog)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!principal.IsAuthenticated)
        {
            return AccessDecision.Allow;
        }

        if (routeTenant is not null
            && !string.Equals(routeTenant, principal.TenantSlug, StringComparison.Ordinal))
        {
            return AccessDecision.NotFound;
        }

        if (routeCatalog is not null
            && principal.CatalogName is { } narrowedTo
            && !string.Equals(routeCatalog, narrowedTo, StringComparison.Ordinal))
        {
            return AccessDecision.NotFound;
        }

        return AccessDecision.Allow;
    }
}

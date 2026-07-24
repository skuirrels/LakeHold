using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Security;

/// <summary>
///     The resolved caller: who a request acts as, and with what capability. Produced from an
///     <see cref="ApiToken"/> by <see cref="ApiTokenAuthenticator"/>, and the single thing downstream
///     code consults instead of trusting a route segment.
/// </summary>
public interface ILakeholdPrincipal
{
    /// <summary>True when resolved from a token; false for the transitional route-trusting caller.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Capability: acts as a tenant, or provisions the instance.</summary>
    TokenScope Scope { get; }

    /// <summary>Owning tenant id, or null for an instance-scoped principal.</summary>
    int? TenantId { get; }

    /// <summary>Owning tenant slug, or null for an instance-scoped principal, which names no tenant.</summary>
    string? TenantSlug { get; }

    /// <summary>The one catalog this principal is narrowed to, or null for the whole tenant.</summary>
    string? CatalogName { get; }

    /// <summary>Whether the credential grants read-only access.</summary>
    bool IsReadOnly { get; }

    /// <summary>Id of the token this principal was resolved from, for audit; null when unauthenticated.</summary>
    int? TokenId { get; }

    /// <summary>The role this principal holds within its tenant.</summary>
    TokenRole Role { get; }
}

/// <inheritdoc cref="ILakeholdPrincipal"/>
/// <remarks>
///     <paramref name="Role"/> is positioned last with a default so the many call sites that predate
///     roles keep compiling and resolve to <see cref="TokenRole.Owner"/> — the capability every
///     credential had before roles existed.
/// </remarks>
public sealed record LakeholdPrincipal(
    bool IsAuthenticated,
    TokenScope Scope,
    int? TenantId,
    string? TenantSlug,
    string? CatalogName,
    bool IsReadOnly,
    int? TokenId,
    TokenRole Role = TokenRole.Owner) : ILakeholdPrincipal
{
    /// <summary>
    ///     The caller for a request that carried no token. It trusts the route, preserving today's
    ///     behaviour until token issuance and the UI wiring land and enforcement can be required. It is
    ///     never produced when a token is presented — an invalid token is refused, not downgraded.
    /// </summary>
    public static LakeholdPrincipal Legacy { get; } = new(
        IsAuthenticated: false,
        Scope: TokenScope.Tenant,
        TenantId: null,
        TenantSlug: null,
        CatalogName: null,
        IsReadOnly: false,
        TokenId: null);
}

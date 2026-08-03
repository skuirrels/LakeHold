using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Security;

/// <summary>
///     The resolved caller: who a request acts as, and with what capability. Produced from an
///     <see cref="ApiToken"/> by <see cref="ApiTokenAuthenticator"/>, and the single thing downstream
///     code consults instead of trusting a route segment.
/// </summary>
public interface ILakeholdPrincipal
{
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

    /// <summary>Whether this is the configured credential-less, read-only demo identity.</summary>
    bool IsDemo { get; }
}

/// <inheritdoc cref="ILakeholdPrincipal"/>
/// <remarks>
///     Every principal is authenticated by construction. There is deliberately no representation of
///     "a caller we could not identify": such a request is refused before a principal exists, so no
///     downstream policy has to remember to check. A route-trusting principal used to live here,
///     and while it did, both policies below began by allowing everything it asked for.
///     <para>
///         <paramref name="Role"/> is positioned last with a default so the many call sites that
///         predate roles keep compiling and resolve to <see cref="TokenRole.Owner"/> — the capability
///         every credential had before roles existed.
///     </para>
/// </remarks>
public sealed record LakeholdPrincipal(
    TokenScope Scope,
    int? TenantId,
    string? TenantSlug,
    string? CatalogName,
    bool IsReadOnly,
    int? TokenId,
    TokenRole Role = TokenRole.Owner,
    bool IsDemo = false) : ILakeholdPrincipal
{
    /// <summary>
    ///     A credential-less visitor constrained to one tenant and catalog and attached read-only,
    ///     used only where an operator has deliberately published a demo catalog. It is a real
    ///     subject-scoped identity, not a bypass: it names exactly one tenant and one catalog and can
    ///     never write.
    /// </summary>
    public static LakeholdPrincipal Demo(string tenant, string catalog) => new(
        Scope: TokenScope.Tenant,
        TenantId: null,
        TenantSlug: tenant,
        CatalogName: catalog,
        IsReadOnly: true,
        TokenId: null,
        Role: TokenRole.Reader,
        IsDemo: true);
}

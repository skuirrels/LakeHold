using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Security;

/// <summary>The outcome of checking a principal against a declared <see cref="Capability"/>.</summary>
/// <remarks>
///     <see cref="NotFound"/> is deliberately the zero value, so a
///     <see langword="default"/>-constructed <see cref="CapabilityDecision"/> refuses rather than
///     allows. A decision type whose default means "allowed" fails open.
/// </remarks>
public enum CapabilityOutcome
{
    /// <summary>The request names a tenant or catalog this principal cannot reach. Reported as a 404.</summary>
    NotFound,

    /// <summary>The principal is reachable but lacks the capability. Reported as a 403.</summary>
    Forbidden,

    /// <summary>The principal may proceed.</summary>
    Allowed,
}

/// <summary>A <see cref="CapabilityOutcome"/> and, when it is a refusal that may say why, the reason.</summary>
public readonly record struct CapabilityDecision
{
    private CapabilityDecision(CapabilityOutcome outcome, string? detail)
    {
        Outcome = outcome;
        Detail = detail;
    }

    /// <summary>What the policy decided.</summary>
    public CapabilityOutcome Outcome { get; }

    /// <summary>
    ///     Why a <see cref="CapabilityOutcome.Forbidden"/> decision was refused, safe to return to the
    ///     caller. Null for every other outcome — a <see cref="CapabilityOutcome.NotFound"/> explains
    ///     nothing on purpose, because explaining it would confirm what it is hiding.
    /// </summary>
    public string? Detail { get; }

    /// <summary>The principal may proceed.</summary>
    public static CapabilityDecision Allow { get; } = new(CapabilityOutcome.Allowed, detail: null);

    /// <summary>The subject is unreachable; the caller learns nothing further.</summary>
    public static CapabilityDecision NotFound { get; } = new(CapabilityOutcome.NotFound, detail: null);

    /// <summary>The subject is reachable but the capability is not held.</summary>
    public static CapabilityDecision Forbid(string detail) => new(CapabilityOutcome.Forbidden, detail);
}

/// <summary>
///     Decides whether a principal holds a declared <see cref="Capability"/> over a given tenant
///     and catalog. The one place those rules live, for every transport that needs them.
/// </summary>
/// <remarks>
///     This is transport-neutral by design and not by accident. The HTTP API reads a route's declared
///     capability from endpoint metadata and its subject from route values; other surfaces carry both
///     differently — an MCP tool declares a capability and takes its tenant as an argument, and there
///     is no route to read. Were each transport to enforce the rules itself, the ordering below would
///     drift between them, and that ordering is the security property: <b>subject is checked before
///     capability</b>, so an unreachable tenant is a <see cref="CapabilityOutcome.NotFound"/> whatever
///     role the caller holds. A <see cref="CapabilityOutcome.Forbidden"/> would confirm the tenant
///     exists, which is information the caller has no right to.
/// </remarks>
public static class CapabilityPolicy
{
    /// <summary>
    ///     Decides whether <paramref name="principal"/> may exercise <paramref name="capability"/>
    ///     against <paramref name="tenant"/> and <paramref name="catalog"/>.
    /// </summary>
    /// <remarks>
    ///     An unauthenticated (route-trusting) caller is always allowed — the transitional open path
    ///     that keeps token-less clients working until authentication is required, at which point such
    ///     a caller is refused before reaching here. A transport that must not offer that path, such as
    ///     an agent-reachable one, refuses a credential-less caller itself rather than asking for a
    ///     different answer here.
    /// </remarks>
    public static CapabilityDecision Evaluate(
        ILakeholdPrincipal principal,
        Capability capability,
        string? tenant,
        string? catalog)
    {
        ArgumentNullException.ThrowIfNull(principal);

        switch (capability)
        {
            case Capability.Listing:
                // The handler scopes what it returns to the principal; reaching it is always allowed.
                return CapabilityDecision.Allow;

            case Capability.Instance:
                return principal.Scope == TokenScope.Instance
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Forbid("This operation requires an instance-scoped credential.");

            case Capability.TenantOwner:
                // Subject first — an unreachable tenant is a 404 whatever the role — then capability.
                if (TenantAccessPolicy.Evaluate(principal, tenant, catalog) is AccessDecision.NotFound)
                {
                    return CapabilityDecision.NotFound;
                }

                return principal.Role == TokenRole.Owner
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Forbid("This operation requires an owner credential.");

            case Capability.TenantWrite:
                if (TenantAccessPolicy.Evaluate(principal, tenant, catalog) is AccessDecision.NotFound)
                {
                    return CapabilityDecision.NotFound;
                }

                return principal.Role is TokenRole.Owner or TokenRole.Editor
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Forbid("This operation requires an editor or owner credential.");

            case Capability.TenantAdmin:
                if (principal.Scope == TokenScope.Instance)
                {
                    return CapabilityDecision.Allow;
                }

                // A tenant principal may administer only its own tenant, and only if it is a full
                // credential — a catalog-narrowed or read-only token is least privilege by design and
                // must not be able to mint a broader one.
                if (!string.Equals(tenant, principal.TenantSlug, StringComparison.Ordinal))
                {
                    return CapabilityDecision.NotFound;
                }

                return principal.CatalogName is null && !principal.IsReadOnly && principal.Role == TokenRole.Owner
                    ? CapabilityDecision.Allow
                    : CapabilityDecision.Forbid("This credential is not permitted to manage tokens.");

            case Capability.TenantData:
            default:
                return TenantAccessPolicy.Evaluate(principal, tenant, catalog) is AccessDecision.NotFound
                    ? CapabilityDecision.NotFound
                    : CapabilityDecision.Allow;
        }
    }
}

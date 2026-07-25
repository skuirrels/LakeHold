namespace Lakehold.ControlPlane.Security;

/// <summary>
///     What kind of credential a caller must present, so one policy can guard data routes,
///     tenant-admin routes, and instance-only provisioning routes without a bespoke check on each.
/// </summary>
/// <remarks>
///     The distinction exists because provisioning and data live under the same <c>/api/tenants</c>
///     prefix but demand opposite things of a principal: an instance token must reach
///     <c>POST /api/tenants</c> and must not reach a query route, while a tenant token is the reverse.
///     A single <see cref="TenantAccessPolicy"/> cannot express both, so the caller declares its intent
///     and <see cref="CapabilityPolicy"/> decides.
///     <para>
///         This lives beside the principal model rather than beside the HTTP routes because capability
///         is a property of the credential, not of a transport. An HTTP route declares one as endpoint
///         metadata; a different transport declares it a different way, and both get the same answer
///         from the same rules.
///     </para>
/// </remarks>
public enum RouteCapability
{
    /// <summary>
    ///     Reaches a tenant's data — query, schema, maintenance, eject. Requires a tenant principal
    ///     whose subject matches the one named by the request; an instance principal is refused,
    ///     exactly as <see cref="TenantAccessPolicy"/> already refuses it. The default when a caller
    ///     declares nothing.
    /// </summary>
    TenantData,

    /// <summary>
    ///     Reaches a tenant's data <em>and</em> changes it destructively or exports it — maintenance,
    ///     restore, and eject. Everything <see cref="TenantData"/> requires, plus the owner role: a
    ///     reader queries and an editor writes, but expiring snapshots, deleting data files, and
    ///     producing a full copy of the lakehouse are the owner's to authorise.
    /// </summary>
    TenantOwner,

    /// <summary>
    ///     Administers one tenant — its tokens. Satisfied by an instance principal (which provisions
    ///     any tenant) or by a full tenant principal acting on its own tenant. A catalog-narrowed or
    ///     read-only tenant token is refused: least-privilege credentials do not mint credentials.
    /// </summary>
    TenantAdmin,

    /// <summary>Provisions the instance — create or delete tenants and catalogs. Instance principals only.</summary>
    Instance,

    /// <summary>
    ///     Any principal; the handler scopes the result to what the principal may see. Used by the
    ///     tenant listing, where an instance token sees every tenant and a tenant token sees only its own.
    /// </summary>
    Listing,
}

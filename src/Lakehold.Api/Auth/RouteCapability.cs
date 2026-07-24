using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.Auth;

/// <summary>
///     What kind of credential a route requires, so one filter can guard data routes, tenant-admin
///     routes, and instance-only provisioning routes without a bespoke check on each endpoint.
/// </summary>
/// <remarks>
///     The distinction exists because provisioning and data live under the same <c>/api/tenants</c>
///     prefix but demand opposite things of a principal: an instance token must reach
///     <c>POST /api/tenants</c> and must not reach a query route, while a tenant token is the reverse.
///     A single <see cref="TenantAccessPolicy"/> cannot express both, so the route declares its intent
///     and <see cref="LakeholdAuthorizationFilter"/> enforces the matching rule.
/// </remarks>
public enum RouteCapability
{
    /// <summary>
    ///     Reaches a tenant's data — query, schema, maintenance, eject. Requires a tenant principal
    ///     whose subject matches the route; an instance principal is refused, exactly as
    ///     <see cref="TenantAccessPolicy"/> already refuses it. The default when a route says nothing.
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

/// <summary>Endpoint metadata declaring a route's <see cref="RouteCapability"/>.</summary>
public sealed record RouteCapabilityMetadata(RouteCapability Capability);

/// <summary>Extensions for reading the resolved principal off the request.</summary>
public static class PrincipalHttpExtensions
{
    /// <summary>
    ///     The principal the authorization filter resolved for this request, or
    ///     <see cref="LakeholdPrincipal.Legacy"/> when none was stashed — a route reached without the
    ///     filter, or a token-less request while enforcement is not required.
    /// </summary>
    public static ILakeholdPrincipal GetLakeholdPrincipal(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(LakeholdAuthorizationFilter.PrincipalItemKey, out var value)
            && value is ILakeholdPrincipal principal
            ? principal
            : LakeholdPrincipal.Legacy;
    }

    /// <summary>Attaches a <see cref="RouteCapability"/> to an endpoint, read back by the filter.</summary>
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, RouteCapability capability)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RouteCapabilityMetadata(capability));
        return builder;
    }
}

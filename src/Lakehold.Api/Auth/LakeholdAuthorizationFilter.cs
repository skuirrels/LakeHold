using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Lakehold.Api.Auth;

/// <summary>
///     The HTTP seam for authentication. Resolves the bearer token (or OIDC principal) to a
///     <see cref="ILakeholdPrincipal"/>, enforces the route's declared <see cref="RouteCapability"/>
///     against it, and stashes the principal for downstream use.
/// </summary>
/// <remarks>
///     Applied to the whole <c>/api/tenants</c> group so every path shares one check. Invalid tokens
///     are 401; a route whose tenant or catalog the principal cannot reach is 404 (never 403 — a 403
///     confirms existence, see <see cref="TenantAccessPolicy"/>); a route whose capability the
///     principal lacks is 403. Scoped services are resolved per request from
///     <see cref="HttpContext.RequestServices"/> because the filter itself is a singleton.
/// </remarks>
public sealed class LakeholdAuthorizationFilter : IEndpointFilter
{
    /// <summary><see cref="HttpContext.Items"/> key under which the resolved principal is stored.</summary>
    public const string PrincipalItemKey = "lakehold.principal";

    /// <summary>Prefix that marks a bearer value as a Lakehold API token rather than a JWT.</summary>
    private const string ApiTokenScheme = "lkh_";

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var services = http.RequestServices;
        var options = services.GetRequiredService<IOptions<LakeholdAuthOptions>>().Value;
        var authenticator = services.GetRequiredService<ApiTokenAuthenticator>();

        var bearer = ExtractBearer(http.Request.Headers.Authorization);
        ILakeholdPrincipal principal;

        if (bearer is not null && bearer.StartsWith(ApiTokenScheme, StringComparison.Ordinal))
        {
            // An lkh_ bearer is an API token: resolved or refused, never downgraded to anonymous.
            var result = await authenticator.AuthenticateAsync(bearer, http.RequestAborted).ConfigureAwait(false);
            if (result.Status != TokenAuthStatus.Authenticated)
            {
                return Unauthorized(http);
            }

            principal = result.Principal!;
        }
        else if (OidcPrincipal.TryResolve(http.User, services.GetRequiredService<IOptions<LakeholdOidcOptions>>().Value) is { } oidc)
        {
            // A JWT bearer (or any scheme that populated http.User) the middleware already validated.
            principal = oidc;
        }
        else if (bearer is not null)
        {
            // A bearer was presented but is neither a valid token nor a valid JWT. A presented
            // credential that does not resolve is refused, not treated as anonymous.
            return Unauthorized(http);
        }
        else if (options.RequireAuthentication)
        {
            return Unauthorized(http);
        }
        else
        {
            principal = LakeholdPrincipal.Legacy;
        }

        var routeTenant = http.Request.RouteValues.TryGetValue("tenantSlug", out var t) ? t as string : null;
        var routeCatalog = http.Request.RouteValues.TryGetValue("catalogName", out var c) ? c as string : null;
        var capability = http.GetEndpoint()?.Metadata.GetMetadata<RouteCapabilityMetadata>()?.Capability
            ?? RouteCapability.TenantData;

        if (Enforce(principal, capability, routeTenant, routeCatalog) is { } refusal)
        {
            return refusal;
        }

        http.Items[PrincipalItemKey] = principal;
        return await next(context).ConfigureAwait(false);
    }

    /// <summary>Enforces <paramref name="capability"/>, returning a refusal result or null to allow.</summary>
    /// <remarks>
    ///     An unauthenticated (route-trusting) caller is always allowed — the transitional open path
    ///     that keeps token-less clients working until <see cref="LakeholdAuthOptions.RequireAuthentication"/>
    ///     is set, at which point a token-less request is already refused above and never reaches here.
    /// </remarks>
    private static IResult? Enforce(
        ILakeholdPrincipal principal,
        RouteCapability capability,
        string? routeTenant,
        string? routeCatalog)
    {
        if (!principal.IsAuthenticated)
        {
            return null;
        }

        switch (capability)
        {
            case RouteCapability.Listing:
                // The handler scopes what it returns to the principal; reaching it is always allowed.
                return null;

            case RouteCapability.Instance:
                return principal.Scope == TokenScope.Instance
                    ? null
                    : Forbidden("This operation requires an instance-scoped credential.");

            case RouteCapability.TenantOwner:
                // Subject first — an unreachable tenant is a 404 whatever the role — then capability.
                if (TenantAccessPolicy.Evaluate(principal, routeTenant, routeCatalog) is AccessDecision.NotFound)
                {
                    return NotFound(routeTenant, routeCatalog);
                }

                return principal.Role == TokenRole.Owner
                    ? null
                    : Forbidden("This operation requires an owner credential.");

            case RouteCapability.TenantAdmin:
                if (principal.Scope == TokenScope.Instance)
                {
                    return null;
                }

                // A tenant principal may administer only its own tenant, and only if it is a full
                // credential — a catalog-narrowed or read-only token is least privilege by design and
                // must not be able to mint a broader one.
                if (!string.Equals(routeTenant, principal.TenantSlug, StringComparison.Ordinal))
                {
                    return NotFound(routeTenant, routeCatalog);
                }

                return principal.CatalogName is null && !principal.IsReadOnly && principal.Role == TokenRole.Owner
                    ? null
                    : Forbidden("This credential is not permitted to manage tokens.");

            case RouteCapability.TenantData:
            default:
                return TenantAccessPolicy.Evaluate(principal, routeTenant, routeCatalog) is AccessDecision.NotFound
                    ? NotFound(routeTenant, routeCatalog)
                    : null;
        }
    }

    private static IResult Unauthorized(HttpContext http)
    {
        // Same opaque refusal for a missing, malformed, unknown, revoked, or expired token.
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
    }

    private static IResult Forbidden(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: detail);

    private static IResult NotFound(string? tenant, string? catalog) =>
        catalog is not null
            ? Results.NotFound($"Catalog '{catalog}' was not found for tenant '{tenant}'.")
            : Results.NotFound($"Tenant '{tenant}' was not found.");

    private static string? ExtractBearer(StringValues authorization)
    {
        const string scheme = "Bearer ";
        foreach (var value in authorization)
        {
            if (value is not null && value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var token = value[scheme.Length..].Trim();
                if (token.Length > 0)
                {
                    return token;
                }
            }
        }

        return null;
    }
}

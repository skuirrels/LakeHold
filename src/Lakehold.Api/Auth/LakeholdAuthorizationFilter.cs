using Lakehold.ControlPlane.Model;
using Lakehold.ControlPlane.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Lakehold.Api.Auth;

/// <summary>
///     The HTTP seam for authentication. Resolves the bearer token (or OIDC principal) to a
///     <see cref="ILakeholdPrincipal"/>, enforces the route's declared <see cref="Capability"/>
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
        else if (http.User.Identity?.IsAuthenticated == true || bearer is not null)
        {
            // Either a validated identity Lakehold cannot map to a tenant, or a bearer that is
            // neither a valid token nor a valid JWT. A presented credential that does not resolve is
            // refused; it is never downgraded to a lesser identity.
            return Unauthorized(http);
        }
        else
        {
            // No credential at all. The only thing an anonymous caller may ever be is the demo
            // reader, and only where an operator deliberately published one. Absent that, refused.
            var demoTenant = options.DemoTenant.Trim();
            var demoCatalog = options.DemoCatalog.Trim();
            if (demoTenant.Length == 0 || demoCatalog.Length == 0)
            {
                return Unauthorized(http);
            }

            principal = LakeholdPrincipal.Demo(demoTenant, demoCatalog);
        }

        var routeTenant = http.Request.RouteValues.TryGetValue("tenantSlug", out var t) ? t as string : null;
        var routeCatalog = http.Request.RouteValues.TryGetValue("catalogName", out var c) ? c as string : null;
        var capability = http.GetEndpoint()?.Metadata.GetMetadata<RouteCapabilityMetadata>()?.Capability
            ?? Capability.TenantData;

        var decision = CapabilityPolicy.Evaluate(principal, capability, routeTenant, routeCatalog);
        if (decision.Outcome is not CapabilityOutcome.Allowed)
        {
            return Refuse(decision, routeTenant, routeCatalog);
        }

        http.Items[PrincipalItemKey] = principal;
        return await next(context).ConfigureAwait(false);
    }

    /// <summary>Maps a refusal from <see cref="CapabilityPolicy"/> onto its HTTP shape.</summary>
    /// <remarks>
    ///     This is all the HTTP transport contributes to authorization: the rules, and the ordering
    ///     that makes an unreachable tenant a 404 rather than a 403, belong to the policy so every
    ///     transport shares them.
    /// </remarks>
    private static IResult Refuse(CapabilityDecision decision, string? routeTenant, string? routeCatalog) =>
        decision.Outcome switch
        {
            CapabilityOutcome.Forbidden => Forbidden(decision.Detail ?? "Forbidden."),
            _ => NotFound(routeTenant, routeCatalog),
        };

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

using Lakehold.ControlPlane.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Lakehold.Api.Auth;

/// <summary>
///     The HTTP seam for authentication. Resolves the bearer token to a principal, validates the
///     route's tenant and catalog against it, and stashes the principal for downstream use.
/// </summary>
/// <remarks>
///     Applied to the whole <c>/api/tenants</c> group so every tenant-scoped path shares one check.
///     Invalid tokens are 401; a route the principal cannot reach is 404 (never 403 — see
///     <see cref="TenantAccessPolicy"/>). Scoped services are resolved per request from
///     <see cref="HttpContext.RequestServices"/> because the filter itself is a singleton.
/// </remarks>
public sealed class LakeholdAuthorizationFilter : IEndpointFilter
{
    /// <summary><see cref="HttpContext.Items"/> key under which the resolved principal is stored.</summary>
    public const string PrincipalItemKey = "lakehold.principal";

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
        var result = await authenticator.AuthenticateAsync(bearer, http.RequestAborted).ConfigureAwait(false);

        ILakeholdPrincipal principal;
        switch (result.Status)
        {
            case TokenAuthStatus.Authenticated:
                principal = result.Principal!;
                break;
            case TokenAuthStatus.Invalid:
                return Unauthorized(http);
            default: // NoToken
                if (options.RequireAuthentication)
                {
                    return Unauthorized(http);
                }

                principal = LakeholdPrincipal.Legacy;
                break;
        }

        var routeTenant = http.Request.RouteValues.TryGetValue("tenantSlug", out var t) ? t as string : null;
        var routeCatalog = http.Request.RouteValues.TryGetValue("catalogName", out var c) ? c as string : null;

        if (TenantAccessPolicy.Evaluate(principal, routeTenant, routeCatalog) is AccessDecision.NotFound)
        {
            return NotFound(routeTenant, routeCatalog);
        }

        http.Items[PrincipalItemKey] = principal;
        return await next(context).ConfigureAwait(false);
    }

    private static IResult Unauthorized(HttpContext http)
    {
        // Same opaque refusal for a missing, malformed, unknown, revoked, or expired token.
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
    }

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

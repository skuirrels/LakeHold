using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Security;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Lakehold.Api.Mcp;

/// <summary>
///     Resolves the credential for an MCP request and refuses the request when there is none.
/// </summary>
/// <remarks>
///     Deliberately <em>not</em> <see cref="LakeholdAuthorizationFilter"/>, and the difference is the
///     point. That filter honours <see cref="LakeholdAuthOptions.RequireAuthentication"/>, so a
///     token-less caller falls back to trusting the route while enforcement is off. This one never
///     does: a surface whose purpose is letting an autonomous agent execute SQL cannot also trust the
///     route, whatever the deployment's transitional setting says (invariant 21).
///
///     It resolves identity only. <em>Capability</em> is decided per tool by
///     <see cref="CapabilityPolicy"/>, because one endpoint serves every tool and the tenant arrives
///     as a tool argument rather than as a route value — there is nothing here to check it against.
/// </remarks>
public sealed class McpAuthenticationFilter : IEndpointFilter
{
    /// <summary>Prefix that marks a bearer value as a Lakehold API token rather than a JWT.</summary>
    private const string ApiTokenScheme = "lkh_";

    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var services = http.RequestServices;

        var oidc = services.GetRequiredService<IOptions<LakeholdOidcOptions>>().Value;
        var bearer = ExtractBearer(http.Request.Headers.Authorization);
        ILakeholdPrincipal? principal = null;

        if (bearer is not null && bearer.StartsWith(ApiTokenScheme, StringComparison.Ordinal))
        {
            var authenticator = services.GetRequiredService<ApiTokenAuthenticator>();
            var result = await authenticator.AuthenticateAsync(bearer, http.RequestAborted).ConfigureAwait(false);
            if (result.Status == TokenAuthStatus.Authenticated)
            {
                principal = result.Principal;
            }
        }
        else if (OidcPrincipal.TryResolve(http.User, oidc) is { } fromJwt)
        {
            principal = fromJwt;
        }

        if (principal is null)
        {
            // One opaque refusal for a missing, malformed, unknown, revoked, or expired credential —
            // the same discipline the HTTP filter keeps, for the same reason. What the challenge adds
            // is where to go next: RFC 9728 wants the metadata document cited here, which is how a
            // client that has no credential discovers the authorization server. Only when OIDC is
            // configured, because otherwise there is no such document to cite.
            var mcp = services.GetRequiredService<IOptions<McpOptions>>().Value;
            http.Response.Headers.WWWAuthenticate = oidc.Enabled
                ? $"Bearer resource_metadata=\"{McpResourceMetadata.AbsoluteUri(http, mcp)}\""
                : "Bearer";

            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        http.Items[LakeholdAuthorizationFilter.PrincipalItemKey] = principal;
        return await next(context).ConfigureAwait(false);
    }

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

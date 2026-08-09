using Lakehold.Api.Auth;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lakehold.Api.Mcp;

/// <summary>
///     Serves OAuth 2.0 Protected Resource Metadata (RFC 9728), which the 2026-07-28 MCP revision
///     requires of a server so a client can discover which authorization server to go to.
/// </summary>
/// <remarks>
///     <para>
///         Written as an ordinary endpoint rather than by registering the SDK's
///         <c>McpAuthenticationHandler</c>. That handler is an ASP.NET Core authentication
///         <em>scheme</em>, and it serves the document and the challenge from the middleware's
///         challenge path — but Lakehold does not authenticate through that pipeline. Identity is
///         resolved by an endpoint filter (see <c>docs/AUTHENTICATION.md</c>), and a second scheme
///         whose job overlapped that filter's would be two things deciding one question. The document
///         is the contract here, not the mechanism that emits it, so it is emitted directly and
///         serialised through the SDK's own <see cref="ProtectedResourceMetadata"/> type so the field
///         names are the spec's rather than ours.
///     </para>
///     <para>
///         **Served only when OIDC is configured.** Absent an authority there is no authorization
///         server to name, and a document advertising none would be worse than no document: a client
///         would discover it, learn nothing, and fail differently. Where OIDC is off, API tokens are
///         the only credential and the challenge stays a bare <c>Bearer</c>.
///     </para>
/// </remarks>
public static class McpResourceMetadata
{
    /// <summary>The well-known path RFC 9728 reserves for this document.</summary>
    public const string Path = "/.well-known/oauth-protected-resource";

    /// <summary>Returns the RFC 9728 path-suffixed location for an MCP route.</summary>
    public static string PathForRoute(string route)
    {
        var normalized = "/" + route.Trim().Trim('/');
        return normalized == "/" ? Path : Path + normalized;
    }

    /// <summary>Maps the metadata document when OIDC is configured.</summary>
    /// <remarks>
    ///     Deliberately outside the MCP route group and unauthenticated: a client reads this
    ///     <em>because</em> it does not yet have a credential, so requiring one would be circular.
    ///     It names an issuer and a resource, both already public knowledge, and no tenant.
    /// </remarks>
    public static IEndpointRouteBuilder MapMcpResourceMetadata(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var oidc = app.ServiceProvider.GetRequiredService<IOptions<LakeholdOidcOptions>>().Value;
        if (!oidc.Enabled)
        {
            return app;
        }

        var mcp = app.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
        async Task<IResult> DescribeAsync(
                HttpContext http,
                McpRuntimeSettingsStore store,
                CancellationToken cancellationToken)
        {
            var settings = await store.GetAsync(cancellationToken).ConfigureAwait(false);
            return settings.Enabled
                ? Results.Json(DescribeDocument(http, settings, oidc))
                : Results.NotFound();
        }

        app.MapGet(Path, DescribeAsync)
            .WithTags("Lakehouse")
            .WithSummary("OAuth 2.0 Protected Resource Metadata for the MCP endpoint (RFC 9728).")
            .AllowAnonymous();

        var pathSpecific = PathForRoute(mcp.Route);
        if (!string.Equals(pathSpecific, Path, StringComparison.Ordinal))
        {
            app.MapGet(pathSpecific, DescribeAsync)
                .WithTags("Lakehouse")
                .WithSummary("Path-specific OAuth 2.0 Protected Resource Metadata for the MCP endpoint (RFC 9728).")
                .AllowAnonymous();
        }

        app.MapAuthorizationServerMetadataRedirects(oidc, mcp);
        return app;
    }

    /// <summary>The RFC 8414 and OIDC discovery paths, redirected to the authorization server's own.</summary>
    /// <remarks>
    ///     <para>
    ///         Lakehold is a resource server and publishes no authorization-server metadata of its
    ///         own; the document above names the issuer, and RFC 9728 has the client read the
    ///         authorization server's metadata from <em>there</em>. Observed clients do not all do
    ///         that. At least one reads <c>authorization_servers</c>, then looks for
    ///         authorization-server metadata only on the <em>resource</em> origin, and on finding
    ///         none falls back to the pre-RFC-9728 MCP assumption that the MCP server is its own
    ///         authorization server — sending the operator to <c>https://&lt;lakehold&gt;/authorize</c>,
    ///         which has never existed. A 404 is the honest answer and is precisely what triggers
    ///         that guess.
    ///     </para>
    ///     <para>
    ///         So answer where the client is looking, without inventing a document. A redirect keeps
    ///         the metadata coming from the issuer that actually signs the tokens — the client ends
    ///         up reading the authorization server's own bytes, not a copy of them that could drift
    ///         or lie about its own <c>issuer</c>. The request's own flavour is preserved, so an
    ///         OAuth-only server is asked for <c>oauth-authorization-server</c> and an OIDC one for
    ///         <c>openid-configuration</c>, rather than guessing which the authority publishes.
    ///     </para>
    /// </remarks>
    private static void MapAuthorizationServerMetadataRedirects(
        this IEndpointRouteBuilder app,
        LakeholdOidcOptions oidc,
        McpOptions mcp)
    {
        var authority = oidc.Authority.TrimEnd('/');
        var routeSuffix = "/" + mcp.Route.Trim().Trim('/');

        foreach (var document in (string[])["oauth-authorization-server", "openid-configuration"])
        {
            var wellKnown = "/.well-known/" + document;
            var target = $"{authority}{wellKnown}";

            IResult Redirect() => Results.Redirect(target, permanent: false, preserveMethod: false);

            app.MapGet(wellKnown, Redirect)
                .WithTags("Lakehouse")
                .WithSummary($"Redirects {document} discovery to the configured authorization server.")
                .AllowAnonymous();

            if (routeSuffix != "/")
            {
                app.MapGet(wellKnown + routeSuffix, Redirect)
                    .WithTags("Lakehouse")
                    .WithSummary($"Redirects path-suffixed {document} discovery to the configured authorization server.")
                    .AllowAnonymous();
            }
        }
    }

    /// <summary>The absolute URI of this document, for a <c>WWW-Authenticate</c> challenge to cite.</summary>
    public static string AbsoluteUri(HttpContext http, McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        return Absolute(http, options, Path).ToString();
    }

    /// <summary>The metadata URI using the currently persisted public address.</summary>
    public static string AbsoluteUri(HttpContext http, McpRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        return Absolute(http, settings.PublicBaseUrl, Path).ToString();
    }

    /// <summary>The exact RFC 8707 resource identifier an MCP access token must target.</summary>
    public static string AbsoluteResourceUri(HttpContext http, McpRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        return Absolute(http, settings.PublicBaseUrl, settings.Route).ToString();
    }

    /// <summary>
    ///     Resolves a path against the address <em>clients</em> use, which is not always the address
    ///     this process was called on.
    /// </summary>
    /// <remarks>
    ///     <see cref="McpOptions.PublicBaseUrl"/> wins where an operator set it, because behind a
    ///     reverse proxy the request describes the internal hop and a client cannot follow that. The
    ///     request's own <c>PathBase</c> is honoured in the fallback so a path-prefixed host still
    ///     produces a reachable URL.
    /// </remarks>
    private static Uri Absolute(HttpContext http, McpOptions options, string path)
        => Absolute(http, options.PublicBaseUrl, path);

    private static Uri Absolute(HttpContext http, string publicBaseUrl, string path)
    {
        if (!string.IsNullOrWhiteSpace(publicBaseUrl)
            && Uri.TryCreate(publicBaseUrl.TrimEnd('/') + path, UriKind.Absolute, out var declared))
        {
            return declared;
        }

        var request = http.Request;
        return new UriBuilder(request.Scheme, request.Host.Host)
        {
            Port = request.Host.Port ?? -1,
            Path = request.PathBase.Add(path).Value,
        }.Uri;
    }

    private static ProtectedResourceMetadata Describe(HttpContext http, McpOptions mcp, LakeholdOidcOptions oidc) =>
        new()
        {
            // The resource identifier is the MCP endpoint itself, which is what a client compares
            // against the URL it called and against the audience of the token it obtains.
            Resource = Absolute(http, mcp, mcp.Route).ToString(),
            AuthorizationServers = [oidc.Authority],

            // Header only. Lakehold reads a bearer from the Authorization header and nowhere else —
            // never a query parameter, which would put a credential in access logs and referrers.
            BearerMethodsSupported = ["header"],
            ScopesSupported = oidc.EffectiveMcpScopes.ToArray(),
            ResourceName = "Lakehold MCP",
        };

    private static ProtectedResourceMetadata Describe(
        HttpContext http,
        McpRuntimeSettings mcp,
        LakeholdOidcOptions oidc) =>
        new()
        {
            Resource = Absolute(http, mcp.PublicBaseUrl, mcp.Route).ToString(),
            AuthorizationServers = [oidc.Authority],
            BearerMethodsSupported = ["header"],
            ScopesSupported = oidc.EffectiveMcpScopes.ToArray(),
            ResourceName = "Lakehold MCP",
        };

    private static JsonObject DescribeDocument(
        HttpContext http,
        McpRuntimeSettings mcp,
        LakeholdOidcOptions oidc)
    {
        var document = JsonSerializer.SerializeToNode(Describe(http, mcp, oidc))!.AsObject();
        if (!string.IsNullOrWhiteSpace(oidc.McpClientId))
        {
            // RFC 9728 permits extension members. MCP clients that support an operator-declared
            // registration can use this id; others safely ignore the member and follow DCR.
            document["client_id"] = oidc.McpClientId;
        }

        return document;
    }
}

namespace Lakehold.Api.Auth;

/// <summary>
///     OpenID Connect configuration, bound from <c>Lakehold:Oidc</c>. Empty <see cref="Authority"/>
///     leaves the whole path off, which is what keeps the air-gapped story intact — a deployment that
///     never sets an authority never takes a dependency on an external identity provider.
/// </summary>
public sealed class LakeholdOidcOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Lakehold:Oidc";

    /// <summary>
    ///     The OIDC authority (issuer) the JWT is validated against — Keycloak, Entra, Authentik,
    ///     Auth0. Empty disables OIDC entirely.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The audience a token must carry to be accepted by the HTTP API and Workbench.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    ///     Where to fetch the discovery document, when that differs from <see cref="Authority"/>.
    /// </summary>
    /// <remarks>
    ///     The browser and the server frequently reach an identity provider by different names — a
    ///     container network versus a published port, an internal service name versus a public
    ///     hostname. The issuer is baked into every token and must stay the name the browser used,
    ///     so it cannot simply be changed to whichever address the server can reach. This resolves
    ///     that: the authority remains the public issuer, and metadata is fetched from here.
    ///     <para>
    ///         Empty derives it from <see cref="Authority"/>, which is correct whenever both sides
    ///         reach the provider at the same address.
    ///     </para>
    /// </remarks>
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>Public client identifier used by the Workbench authorization-code flow.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    ///     Optional confidential-client secret. Supply it through environment or a secret store,
    ///     never an application settings file.
    /// </summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Whether HTTPS metadata is required of the authority. Only relax this against a local IdP.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    ///     Claim naming the tenant used to admit a first-time human. After admission, the durable
    ///     <c>TenantMember</c> row is authoritative for role and status.
    /// </summary>
    public string TenantClaim { get; set; } = "tenant";

    /// <summary>Claim naming the caller's role within the tenant, if the IdP emits one.</summary>
    public string RoleClaim { get; set; } = "role";

    /// <summary>Claim whose configured value grants instance-wide administration.</summary>
    public string SystemAdminClaim { get; set; } = "lakehold_admin";

    /// <summary>Exact claim value granting instance-wide administration.</summary>
    public string SystemAdminValue { get; set; } = "true";

    /// <summary>Additional scopes requested by the browser sign-in flow.</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    ///     Public, PKCE-only client registered for MCP clients at the configured identity provider.
    ///     Empty leaves MCP clients to use dynamic registration or explicit client configuration.
    /// </summary>
    public string McpClientId { get; set; } = string.Empty;

    /// <summary>
    ///     Scopes requested by MCP clients. Empty reuses <see cref="Scopes"/> so the membership
    ///     claims needed by <c>MemberDirectory</c> are not accidentally omitted.
    /// </summary>
    public string[] McpScopes { get; set; } = [];

    /// <summary>Whether an authority is configured at all.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Authority);

    /// <summary>Whether interactive browser sign-in has enough configuration to run.</summary>
    public bool BrowserLoginEnabled => Enabled && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>The normalized scopes advertised for the MCP resource.</summary>
    public IReadOnlyList<string> EffectiveMcpScopes =>
        (McpScopes.Length > 0 ? McpScopes : Scopes)
        .Where(scope => !string.IsNullOrWhiteSpace(scope))
        .Select(scope => scope.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>Refuses an issuer configuration that cannot prove a token was issued for LakeHold.</summary>
    public void ValidateForStartup()
    {
        if (Authority.Length > 0 && !Enabled)
        {
            throw new InvalidOperationException(
                "Lakehold:Oidc:Authority cannot contain only whitespace. Remove it to disable OIDC.");
        }

        if (Enabled && string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException(
                "Lakehold:Oidc:Audience is required when Lakehold:Oidc:Authority is configured. "
                + "Set it to this deployment's API or Workbench audience; MCP tokens are additionally "
                + "validated against the advertised MCP resource identifier.");
        }
    }
}

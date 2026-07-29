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

    /// <summary>The audience a token must carry to be accepted. Empty skips audience validation.</summary>
    public string Audience { get; set; } = string.Empty;

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
    ///     Claim naming the tenant a human belongs to. The simplest honest mapping — a single claim —
    ///     with a <c>TenantMember</c> table deferred until per-user membership is actually asked for.
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

    /// <summary>Whether an authority is configured at all.</summary>
    public bool Enabled => Authority.Length > 0;

    /// <summary>Whether interactive browser sign-in has enough configuration to run.</summary>
    public bool BrowserLoginEnabled => Enabled && ClientId.Length > 0;
}

namespace Lakehold.Api.Auth;

/// <summary>Which of the two ways this deployment gets its people.</summary>
/// <remarks>
///     These are genuinely different deployments rather than one with a feature toggled, and saying
///     so in a single setting is what lets every surface answer the question consistently — the
///     Users page, the setup guide, and the API all read the same value instead of each inferring
///     the mode from whether some credential happens to be present.
/// </remarks>
public enum IdentityMode
{
    /// <summary>
    ///     A directory that already holds everybody, and existed before Lakehold did.
    /// </summary>
    /// <remarks>
    ///     The default, and the one where creating users from here would be wrong: Lakehold does not
    ///     own that directory. People arrive by signing in, and administration here decides what they
    ///     reach, not whether they exist.
    /// </remarks>
    Sso = 0,

    /// <summary>
    ///     A provider deployed for Lakehold, holding nobody else, administered from Lakehold.
    /// </summary>
    /// <remarks>
    ///     Here "add them in your provider" means learning another product's admin console to onboard
    ///     a colleague, for an identity that exists only to reach this one. In this mode Lakehold
    ///     creates the user through the provider's admin API and owns the whole flow.
    /// </remarks>
    BuiltIn = 1,
}

/// <summary>The admin-API credential used in <see cref="IdentityMode.BuiltIn"/>.</summary>
/// <remarks>
///     <para>
///         **What built-in mode costs.** To create a user in a provider, Lakehold must hold a
///         credential that can create users in that provider. In SSO mode it holds none, and that is
///         a property worth naming before giving it up — whoever compromises Lakehold there cannot
///         mint an identity. So the credential is asked to carry exactly one capability, manage users
///         in one realm, and <see cref="ClientSecret"/> follows the same rule as every other secret
///         here: environment or secret store, never an application settings file.
///     </para>
/// </remarks>
public sealed class UserProvisioningOptions
{
    /// <summary>
    ///     Base URL of the provider's administrative API, when it differs from the OIDC authority.
    /// </summary>
    /// <remarks>
    ///     Empty means "derive it from the authority", which is right for Keycloak, where the realm
    ///     issuer and the admin API share an origin. A provider that separates them needs this set.
    /// </remarks>
    public string AdminBaseUrl { get; set; } = string.Empty;

    /// <summary>Realm or directory the user is created in. Empty derives it from the authority.</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>Client id of the service account permitted to manage users.</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Its secret. Environment or secret store only.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    ///     Ask the provider to email its own invitation rather than returning a temporary password.
    /// </summary>
    /// <remarks>
    ///     Preferable where the provider can send mail: nothing secret is returned through Lakehold
    ///     at all. Off by default because it makes working SMTP a prerequisite for adding a
    ///     colleague, and a silent non-delivery is worse than a password an administrator can read
    ///     out.
    /// </remarks>
    public bool UseProviderEmail { get; set; }

    /// <summary>Whether the credential needed to create a user is present.</summary>
    /// <remarks>
    ///     Separate from the mode on purpose. Built-in mode without a credential is a misconfiguration
    ///     an operator should be told about, not a surface that silently behaves like SSO mode.
    /// </remarks>
    public bool HasCredential =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

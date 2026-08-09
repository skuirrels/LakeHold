using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Auth;

/// <summary>Creates a user through Keycloak's admin API.</summary>
/// <remarks>
///     <para>
///         The one implementation of <see cref="IUserProvisioner"/> today, and the only file in the
///         product that knows which identity provider is in use. Everything else reads the interface,
///         so a deployment on another provider needs a sibling of this class and no other change.
///     </para>
///     <para>
///         Two rules govern what leaves this file. A provider's error body can echo the request that
///         caused it, and these requests contain a password, so a failure is summarised by status
///         rather than forwarded. And the temporary password is generated here, returned once, and
///         never written anywhere — not to a log, not to the control plane, not into the exception
///         message.
///     </para>
/// </remarks>
public sealed class KeycloakUserProvisioner(
    IHttpClientFactory clients,
    IOptions<LakeholdOidcOptions> configured,
    ILogger<KeycloakUserProvisioner> logger) : IUserProvisioner
{
    /// <summary>Named client, so a deployment can attach its own handler or certificate policy.</summary>
    public const string HttpClientName = "lakehold-user-provisioning";

    private readonly LakeholdOidcOptions oidc = configured.Value;

    /// <inheritdoc />
    public bool IsAvailable => oidc.UserProvisioningEnabled;

    /// <inheritdoc />
    public async Task<ProvisionedUser> CreateAsync(
        NewUserRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAvailable)
        {
            throw new UserProvisioningException(
                "Creating users is not configured on this node. It requires built-in identity mode "
                + "and a provisioning credential.");
        }

        var token = await ServiceAccountTokenAsync(cancellationToken).ConfigureAwait(false);
        var password = oidc.Provisioning.UseProviderEmail ? null : GeneratePassword();

        using var client = clients.CreateClient(HttpClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (given, family) = SplitName(request.DisplayName);
        var payload = new Dictionary<string, object?>
        {
            ["username"] = request.Username,
            ["enabled"] = true,
            ["email"] = request.Email,
            // Unverified unless the provider verifies it. Claiming otherwise would let an address
            // nobody proved control of stand in for identity at any provider that trusts the flag.
            ["emailVerified"] = false,
            ["firstName"] = given,
            ["lastName"] = family,
            // The password is temporary either way: with an emailed invitation there is none, and
            // otherwise the provider forces a change before the account is usable.
            ["requiredActions"] = oidc.Provisioning.UseProviderEmail
                ? (string[])["UPDATE_PASSWORD"]
                : (string[])["UPDATE_PASSWORD"],
        };

        if (password is not null)
        {
            payload["credentials"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "password",
                    ["value"] = password,
                    ["temporary"] = true,
                },
            };
        }

        var usersUrl = $"{AdminRealmUrl()}/users";
        using var response = await client
            .PostAsJsonAsync(usersUrl, payload, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new UserProvisioningException(
                $"A user named '{request.Username}' already exists in the identity provider.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Status only. The body is the provider's, and the request it describes carried a
            // password.
            ProvisioningLog.CreateRefused(logger, (int)response.StatusCode);
            throw new UserProvisioningException(
                $"The identity provider refused to create the user ({(int)response.StatusCode}). "
                + "Check that the provisioning client can manage users in this realm.");
        }

        var subject = SubjectFromLocation(response.Headers.Location)
                      ?? await FindSubjectAsync(client, request.Username, cancellationToken)
                          .ConfigureAwait(false);

        if (subject is null)
        {
            throw new UserProvisioningException(
                "The user was created but the provider did not report its identifier, so the "
                + "membership could not be bound to it. Find the user in the provider and have them "
                + "sign in once.");
        }

        if (oidc.Provisioning.UseProviderEmail)
        {
            await SendInvitationAsync(client, subject, cancellationToken).ConfigureAwait(false);
        }

        ProvisioningLog.Created(logger, subject);
        return new ProvisionedUser(subject, password);
    }

    /// <summary>Client-credentials token for the provisioning service account.</summary>
    private async Task<string> ServiceAccountTokenAsync(CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient(HttpClientName);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = oidc.Provisioning.ClientId,
            ["client_secret"] = oidc.Provisioning.ClientSecret,
        });

        using var response = await client
            .PostAsync(new Uri($"{RealmUrl()}/protocol/openid-connect/token"), form, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ProvisioningLog.CredentialRejected(logger, (int)response.StatusCode);
            throw new UserProvisioningException(
                "The identity provider rejected the provisioning credential. Check the client id and "
                + "secret, and that the client has a service account.");
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return document.RootElement.TryGetProperty("access_token", out var value)
               && value.GetString() is { Length: > 0 } token
            ? token
            : throw new UserProvisioningException(
                "The identity provider returned no access token for the provisioning credential.");
    }

    private async Task SendInvitationAsync(
        HttpClient client,
        string subject,
        CancellationToken cancellationToken)
    {
        using var response = await client
            .PutAsJsonAsync(
                $"{AdminRealmUrl()}/users/{subject}/execute-actions-email",
                (string[])["UPDATE_PASSWORD"],
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The identity exists; only the invitation failed. Saying so precisely is what stops an
            // administrator assuming the whole operation rolled back.
            throw new UserProvisioningException(
                $"The user was created, but the provider could not send the invitation email "
                + $"({(int)response.StatusCode}). Check the provider's SMTP settings, then resend it "
                + "from there.");
        }
    }

    private async Task<string?> FindSubjectAsync(
        HttpClient client,
        string username,
        CancellationToken cancellationToken)
    {
        var url = $"{AdminRealmUrl()}/users?exact=true&username={Uri.EscapeDataString(username)}";
        using var response = await client.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        return document.RootElement.ValueKind == JsonValueKind.Array
               && document.RootElement.GetArrayLength() > 0
               && document.RootElement[0].TryGetProperty("id", out var id)
            ? id.GetString()
            : null;
    }

    /// <summary>Keycloak reports the new user's id only in the <c>Location</c> header.</summary>
    private static string? SubjectFromLocation(Uri? location)
    {
        var segment = location?.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? null : segment;
    }

    /// <summary>The realm's issuer URL — where tokens come from.</summary>
    private string RealmUrl()
    {
        var configuredRealm = oidc.Provisioning.Realm;
        if (configuredRealm.Length == 0)
        {
            return oidc.Authority.TrimEnd('/');
        }

        var origin = ProviderOrigin();
        return $"{origin}/realms/{Uri.EscapeDataString(configuredRealm)}";
    }

    /// <summary>The admin API's realm URL, which Keycloak places under <c>/admin</c>.</summary>
    private string AdminRealmUrl() =>
        $"{ProviderOrigin()}/admin/realms/{Uri.EscapeDataString(RealmName())}";

    private string RealmName()
    {
        if (oidc.Provisioning.Realm is { Length: > 0 } configuredRealm)
        {
            return configuredRealm;
        }

        // Derive it from the authority: `https://host/realms/<name>` is Keycloak's issuer shape.
        var authority = oidc.Authority.TrimEnd('/');
        var marker = authority.LastIndexOf("/realms/", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? string.Empty : authority[(marker + "/realms/".Length)..];
    }

    private string ProviderOrigin()
    {
        if (oidc.Provisioning.AdminBaseUrl is { Length: > 0 } configuredBase)
        {
            return configuredBase.TrimEnd('/');
        }

        var authority = oidc.Authority.TrimEnd('/');
        var marker = authority.LastIndexOf("/realms/", StringComparison.OrdinalIgnoreCase);
        return marker < 0 ? authority : authority[..marker];
    }

    /// <summary>Splits a display name the way a provider expects given and family names.</summary>
    private static (string? Given, string? Family) SplitName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return (null, null);
        }

        var parts = displayName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 1 ? (parts[0], null) : (parts[0], parts[1]);
    }

    /// <summary>
    ///     A one-time password, from a cryptographic source and long enough that its strength does
    ///     not depend on the provider's policy.
    /// </summary>
    /// <remarks>
    ///     The alphabet deliberately excludes characters that are misread when a password is read
    ///     aloud or copied off a screen, which is exactly how this one travels. It is temporary and
    ///     the provider forces a change at first sign-in, so legibility beats entropy per character —
    ///     the length is what carries the strength.
    /// </remarks>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var characters = new char[24];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(characters);
    }
}

/// <summary>Source-generated log messages, per the analyzer rules this project enforces.</summary>
/// <remarks>
///     Every message here carries a status code or a subject and never a request body: the requests
///     this class makes contain a password, and a provider's error response can echo them back.
/// </remarks>
internal static partial class ProvisioningLog
{
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Information,
        Message = "Created identity {Subject} in the configured provider")]
    public static partial void Created(ILogger logger, string subject);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Identity provider refused to create a user: {Status}")]
    public static partial void CreateRefused(ILogger logger, int status);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Identity provider rejected the provisioning credential: {Status}")]
    public static partial void CredentialRejected(ILogger logger, int status);
}

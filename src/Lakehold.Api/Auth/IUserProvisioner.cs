namespace Lakehold.Api.Auth;

/// <summary>The outcome of creating an identity: who it is, and how they first get in.</summary>
/// <param name="Subject">The provider's stable identifier, which is what membership is keyed on.</param>
/// <param name="TemporaryPassword">
///     A one-time password to read out, or <see langword="null"/> where the provider was asked to
///     send its own invitation. Never persisted and never logged; it exists only long enough to
///     reach the administrator who asked for it.
/// </param>
public sealed record ProvisionedUser(string Subject, string? TemporaryPassword);

/// <summary>Creates an identity in the configured provider.</summary>
/// <remarks>
///     <para>
///         A seam rather than a Keycloak client, because the rest of this codebase knows nothing
///         about which provider is in use and should keep not knowing. The one implementation today
///         speaks Keycloak's admin API; a deployment on Entra or Authentik needs another one behind
///         this interface and nothing else changes.
///     </para>
///     <para>
///         Deliberately narrow. Creating a user is the only thing an operator cannot reasonably do
///         elsewhere in the flow of adding a colleague; resets, MFA, and directory synchronisation
///         all stay with the provider, and widening this interface is how Lakehold would drift into
///         being a directory it has no wish to be.
///     </para>
/// </remarks>
public interface IUserProvisioner
{
    /// <summary>Whether provisioning is available on this deployment.</summary>
    bool IsAvailable { get; }

    /// <summary>Creates the identity and returns its subject.</summary>
    /// <exception cref="UserProvisioningException">
    ///     The provider refused, or is unreachable. The message is safe to show an administrator and
    ///     carries nothing from the provider's response that could disclose a credential.
    /// </exception>
    Task<ProvisionedUser> CreateAsync(
        NewUserRequest request,
        CancellationToken cancellationToken);
}

/// <summary>The identity to create.</summary>
/// <param name="Username">Sign-in name. Required, because every provider keys on one.</param>
/// <param name="Email">Optional address; also where an invitation is sent when one is.</param>
/// <param name="DisplayName">Optional human name, split into given and family names per provider.</param>
public sealed record NewUserRequest(string Username, string? Email, string? DisplayName);

/// <summary>A provider refused to create the identity, or could not be reached.</summary>
/// <remarks>
///     Carries a message written for the administrator reading it. Provider responses are summarised
///     rather than forwarded: an admin API's error body can echo request material, and this one's
///     requests contain a password.
/// </remarks>
public sealed class UserProvisioningException(string message, Exception? inner = null)
    : Exception(message, inner);

using Microsoft.Extensions.Logging;
using Lakehold.Api.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>
///     Which deployments create identities, and where the provisioner looks when they do.
/// </summary>
/// <remarks>
///     The mode is the security boundary here. Under SSO Lakehold holds no credential able to create
///     a user, and these assert that no combination of half-configuration quietly produces one.
/// </remarks>
public sealed class UserProvisioningTests
{
    private static LakeholdOidcOptions Options(
        IdentityMode mode = IdentityMode.BuiltIn,
        string clientId = "lakehold-provisioner",
        string clientSecret = "secret",
        string authority = "https://idp.example.com/realms/lakehold") =>
        new()
        {
            Authority = authority,
            Audience = "lakehold-api",
            ClientId = "lakehold-workbench",
            Mode = mode,
            Provisioning = new UserProvisioningOptions
            {
                ClientId = clientId,
                ClientSecret = clientSecret,
            },
        };

    [Fact]
    public void Sso_mode_never_provisions_however_configured()
    {
        // The credential being present is not consent to use it. Someone who switches a deployment
        // back to SSO has said the directory is not ours to write to, and leftover configuration must
        // not keep the surface alive.
        var options = Options(IdentityMode.Sso);

        Assert.False(options.UserProvisioningEnabled);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("lakehold-provisioner", "")]
    [InlineData("", "")]
    public void Built_in_mode_without_a_credential_is_not_enabled(string clientId, string secret)
    {
        // Reporting the surface as available without a credential produces a form that fails at
        // submit. An operator should be told what is missing instead.
        var options = Options(clientId: clientId, clientSecret: secret);

        Assert.False(options.UserProvisioningEnabled);
        Assert.False(options.Provisioning.HasCredential);
    }

    [Fact]
    public void Built_in_mode_with_a_credential_and_browser_login_is_enabled()
    {
        Assert.True(Options().UserProvisioningEnabled);
    }

    [Fact]
    public void Provisioning_requires_browser_login_to_be_configured_at_all()
    {
        // No client id means no browser sign-in, so there is nobody to create a user *for*: the
        // person could not sign in afterwards.
        var options = Options();
        options.ClientId = string.Empty;

        Assert.False(options.BrowserLoginEnabled);
        Assert.False(options.UserProvisioningEnabled);
    }

    [Fact]
    public void The_provisioner_reports_itself_unavailable_under_sso()
    {
        var provisioner = new KeycloakUserProvisioner(
            new StubHttpClientFactory(),
            Options(IdentityMode.Sso).Wrapped(),
            NullLoggerOf<KeycloakUserProvisioner>());

        Assert.False(provisioner.IsAvailable);
    }

    [Fact]
    public async Task An_unavailable_provisioner_refuses_before_touching_the_network()
    {
        // The stub throws if any HTTP client is created, so this also proves the refusal happens
        // before a request is built — the credential is never even assembled under SSO.
        var provisioner = new KeycloakUserProvisioner(
            new StubHttpClientFactory(),
            Options(IdentityMode.Sso).Wrapped(),
            NullLoggerOf<KeycloakUserProvisioner>());

        var failure = await Assert.ThrowsAsync<UserProvisioningException>(
            () => provisioner.CreateAsync(new NewUserRequest("ada", null, null), CancellationToken.None));

        Assert.Contains("not configured", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Microsoft.Extensions.Logging.Abstractions.NullLogger<T> NullLoggerOf<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    /// <summary>Fails the test if anything asks it for a client.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException(
                "A disabled provisioner must not reach for the network.");
    }
}

internal static class OptionsExtensions
{
    public static IOptions<LakeholdOidcOptions> Wrapped(this LakeholdOidcOptions options) =>
        Microsoft.Extensions.Options.Options.Create(options);
}

using System.Security.Claims;
using DuckDB.EFCoreProvider.Extensions;
using Lakehold.Api.Auth;
using Lakehold.Api.Endpoints;
using Lakehold.ControlPlane.Data;
using Lakehold.ControlPlane.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Security boundaries for browser-session discovery and post-login redirects.</summary>
public sealed class BrowserAuthenticationEndpointsTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(claim => new Claim(claim.Type, claim.Value))], "oidc"));

    /// <summary>
    ///     A directory over a throwaway control plane. Session resolution consults membership now, so
    ///     it needs one even for an administrator, whose claim short-circuits before any query.
    /// </summary>
    private static ControlPlaneContext Context(string root)
    {
        Directory.CreateDirectory(root);
        var context = new ControlPlaneContext(
            new DbContextOptionsBuilder<ControlPlaneContext>()
                .UseDuckDB($"Data Source={Path.Combine(root, "cp.duckdb")}")
                .Options);
        context.Database.EnsureCreated();
        return context;
    }

    [Fact]
    public async Task Session_reports_an_authenticated_system_administrator_without_exposing_claims()
    {
        var options = Options.Create(new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            ClientId = "lakehold-workbench",
            SystemAdminClaim = "groups",
            SystemAdminValue = "lakehold-administrators",
        });

        var root = Path.Combine(Path.GetTempPath(), "lakehold-session", Guid.NewGuid().ToString("N"));
        await using var context = Context(root);

        var session = await BrowserAuthenticationEndpoints.SessionAsync(
            User(
                ("name", "Ada Administrator"),
                ("groups", "lakehold-administrators"),
                ("unrelated-secret-claim", "must-not-leave")),
            options,
            new MemberDirectory(context, TimeProvider.System),
            CancellationToken.None);

        Assert.True(session.OidcEnabled);
        Assert.True(session.Authenticated);
        Assert.True(session.SystemAdmin);
        Assert.True(session.HasAccess);
        Assert.Equal("Ada Administrator", session.DisplayName);
    }

    [Fact]
    public async Task Session_reports_a_signed_in_person_who_reaches_nothing_as_exactly_that()
    {
        var options = Options.Create(new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            ClientId = "lakehold-workbench",
        });

        var root = Path.Combine(Path.GetTempPath(), "lakehold-session", Guid.NewGuid().ToString("N"));
        await using var context = Context(root);

        var session = await BrowserAuthenticationEndpoints.SessionAsync(
            User(("sub", "newcomer"), ("name", "New Comer")),
            options,
            new MemberDirectory(context, TimeProvider.System),
            CancellationToken.None);

        // Signed in and reaching nothing is a real state: a first arrival awaiting approval. Saying
        // "not authenticated" would send them back to a sign-in they have already completed.
        Assert.True(session.Authenticated);
        Assert.False(session.HasAccess);
        Assert.False(session.SystemAdmin);
        Assert.Equal("New Comer", session.DisplayName);
    }


    [Theory]
    [InlineData(null, "/workbench")]
    [InlineData("", "/workbench")]
    [InlineData("https://attacker.example", "/workbench")]
    [InlineData("//attacker.example", "/workbench")]
    [InlineData("/\\attacker.example", "/workbench")]
    [InlineData("/workbench?view=settings", "/workbench?view=settings")]
    public void Return_urls_are_local_or_replaced_with_the_workbench(
        string? requested,
        string expected)
        => Assert.Equal(expected, BrowserAuthenticationEndpoints.SafeReturnUrl(requested));

    /// <summary>Signing out ends the provider's session, not only Lakehold's.</summary>
    /// <remarks>
    ///     This asserts the scheme list because the scheme list is the whole defect. Signing out of
    ///     the cookie alone left the identity provider's session intact, so **Sign in** silently
    ///     re-authenticated the same person — no account chooser, no way back as somebody else, and
    ///     on a shared machine a sign-out that signed nobody out. Naming the OIDC scheme is what
    ///     turns this into an RP-initiated logout.
    /// </remarks>
    [Fact]
    public void Signing_out_ends_the_provider_session_as_well_as_the_local_one()
    {
        var options = Options.Create(new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            ClientId = "lakehold-workbench",
            Audience = "lakehold-api",
        });

        var result = BrowserAuthenticationEndpoints.Logout("/workbench", options);

        var signOut = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.SignOutHttpResult>(result);
        Assert.Equal(
            [BrowserAuthentication.CookieScheme, BrowserAuthentication.OidcScheme],
            signOut.AuthenticationSchemes);
        Assert.Equal("/workbench", signOut.Properties?.RedirectUri);
    }

    /// <summary>With no browser login configured there is no provider session to end.</summary>
    [Fact]
    public void Signing_out_without_browser_login_configured_is_a_plain_redirect()
    {
        // ClientId empty, so BrowserLoginEnabled is false: challenging a scheme that was never
        // registered would throw rather than sign anybody out.
        var options = Options.Create(new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            Audience = "lakehold-api",
        });

        var result = BrowserAuthenticationEndpoints.Logout("/workbench", options);

        Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult>(result);
    }
}

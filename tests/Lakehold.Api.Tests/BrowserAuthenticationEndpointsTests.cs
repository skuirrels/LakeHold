using System.Security.Claims;
using Lakehold.Api.Auth;
using Lakehold.Api.Endpoints;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lakehold.Api.Tests;

/// <summary>Security boundaries for browser-session discovery and post-login redirects.</summary>
public sealed class BrowserAuthenticationEndpointsTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity([.. claims.Select(claim => new Claim(claim.Type, claim.Value))], "oidc"));

    [Fact]
    public void Session_reports_an_authenticated_system_administrator_without_exposing_claims()
    {
        var options = Options.Create(new LakeholdOidcOptions
        {
            Authority = "https://idp.test",
            ClientId = "lakehold-workbench",
            SystemAdminClaim = "groups",
            SystemAdminValue = "lakehold-administrators",
        });

        var session = BrowserAuthenticationEndpoints.Session(
            User(
                ("name", "Ada Administrator"),
                ("groups", "lakehold-administrators"),
                ("unrelated-secret-claim", "must-not-leave")),
            options);

        Assert.True(session.OidcEnabled);
        Assert.True(session.Authenticated);
        Assert.True(session.SystemAdmin);
        Assert.Equal("Ada Administrator", session.DisplayName);
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
}

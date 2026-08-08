using Lakehold.Api.Auth;
using System.Security.Claims;
using Xunit;

namespace Lakehold.Api.Tests;

public sealed class LakeholdOidcOptionsTests
{
    [Fact]
    public void Configured_authority_requires_an_audience()
    {
        var options = new LakeholdOidcOptions { Authority = "https://idp.example.com" };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateForStartup);

        Assert.Contains("Lakehold:Oidc:Audience", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("https://idp.example.com", "lakehold-api")]
    public void Disabled_or_complete_configuration_is_valid(string authority, string audience)
    {
        var options = new LakeholdOidcOptions { Authority = authority, Audience = audience };

        options.ValidateForStartup();
    }

    [Fact]
    public void Whitespace_does_not_enable_a_broken_oidc_configuration()
    {
        var options = new LakeholdOidcOptions { Authority = "   " };

        var error = Assert.Throws<InvalidOperationException>(options.ValidateForStartup);

        Assert.Contains("whitespace", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audience_matching_is_exact_and_accepts_any_one_of_multiple_audiences()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("aud", "lakehold-api"),
            new Claim("aud", "https://lakehold.example.com/mcp"),
        ]));

        Assert.True(LakeholdAudience.Matches(principal, "https://lakehold.example.com/mcp"));
        Assert.False(LakeholdAudience.Matches(principal, "https://lakehold.example.com/mcp/"));
    }
}

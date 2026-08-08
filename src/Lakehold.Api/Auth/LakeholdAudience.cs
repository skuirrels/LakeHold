using System.Security.Claims;

namespace Lakehold.Api.Auth;

/// <summary>Exact audience matching shared by the JWT pipeline and the MCP fail-closed check.</summary>
internal static class LakeholdAudience
{
    public static bool Matches(ClaimsPrincipal principal, string expected)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        return principal.FindAll("aud")
            .Any(claim => string.Equals(claim.Value, expected, StringComparison.Ordinal));
    }
}

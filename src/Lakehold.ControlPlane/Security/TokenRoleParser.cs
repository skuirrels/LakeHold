using Lakehold.ControlPlane.Model;

namespace Lakehold.ControlPlane.Security;

/// <summary>Parses a role name — from an OIDC claim or an issuance request — into a <see cref="TokenRole"/>.</summary>
public static class TokenRoleParser
{
    /// <summary>
    ///     Maps <paramref name="value"/> to a role, returning <paramref name="fallback"/> for a null,
    ///     blank, or unrecognised value. Common synonyms are accepted so an IdP's own vocabulary
    ///     (<c>write</c>, <c>readonly</c>) maps without configuration.
    /// </summary>
    public static TokenRole Parse(string? value, TokenRole fallback = TokenRole.Reader)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "owner" or "admin" => TokenRole.Owner,
            "editor" or "writer" or "write" or "readwrite" => TokenRole.Editor,
            "reader" or "read" or "readonly" or "read-only" or "viewer" => TokenRole.Reader,
            _ => fallback,
        };
    }
}

using Lakehold.ControlPlane.Data;
using Microsoft.EntityFrameworkCore;

namespace Lakehold.ControlPlane.Security;

/// <summary>Whether a request carried a token, and whether it resolved.</summary>
public enum TokenAuthStatus
{
    /// <summary>No credential was presented.</summary>
    NoToken,

    /// <summary>A credential was presented but is malformed, unknown, revoked, or expired.</summary>
    Invalid,

    /// <summary>A credential resolved to a live principal.</summary>
    Authenticated,
}

/// <summary>The result of resolving a presented token.</summary>
public sealed record TokenAuthResult(TokenAuthStatus Status, LakeholdPrincipal? Principal)
{
    /// <summary>No credential was presented.</summary>
    public static readonly TokenAuthResult NoToken = new(TokenAuthStatus.NoToken, null);

    /// <summary>A credential was presented but did not resolve.</summary>
    public static readonly TokenAuthResult Invalid = new(TokenAuthStatus.Invalid, null);

    /// <summary>A credential resolved to <paramref name="principal"/>.</summary>
    public static TokenAuthResult Authenticated(LakeholdPrincipal principal) =>
        new(TokenAuthStatus.Authenticated, principal);
}

/// <summary>
///     Resolves a presented bearer token to a <see cref="LakeholdPrincipal"/>, or refuses it.
/// </summary>
/// <remarks>
///     The lookup is narrowed by the token's public prefix to a tenant's candidate rows, then each is
///     compared in constant time (<see cref="ApiTokenFactory.Verify"/>). Malformed, unknown, revoked,
///     and expired tokens are all reported identically as <see cref="TokenAuthStatus.Invalid"/> — the
///     caller learns only that the credential did not resolve, never which reason. This never writes:
///     <c>LastUsedUtc</c> is deliberately not touched on the request path.
/// </remarks>
public sealed class ApiTokenAuthenticator(ControlPlaneContext context, TimeProvider clock)
{
    /// <summary>Resolves <paramref name="presentedToken"/>, or reports why it was refused.</summary>
    public async Task<TokenAuthResult> AuthenticateAsync(string? presentedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return TokenAuthResult.NoToken;
        }

        if (!ApiTokenFactory.TryGetPrefix(presentedToken, out var prefix))
        {
            return TokenAuthResult.Invalid;
        }

        var candidates = await context.ApiTokens
            .AsNoTracking()
            .Include(t => t.Tenant)
            .Where(t => t.Prefix == prefix)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var match = candidates.FirstOrDefault(t => ApiTokenFactory.Verify(presentedToken, t.SecretHash));
        if (match is null)
        {
            return TokenAuthResult.Invalid;
        }

        // Revoked and expired are refused exactly as an unknown token is.
        var now = clock.GetUtcNow();
        if (match.RevokedUtc is not null || (match.ExpiresUtc is { } expiry && expiry <= now))
        {
            return TokenAuthResult.Invalid;
        }

        return TokenAuthResult.Authenticated(new LakeholdPrincipal(
            IsAuthenticated: true,
            Scope: match.Scope,
            TenantId: match.TenantId,
            TenantSlug: match.Tenant?.Slug,
            CatalogName: match.CatalogName,
            IsReadOnly: match.ReadOnly,
            TokenId: match.Id));
    }
}

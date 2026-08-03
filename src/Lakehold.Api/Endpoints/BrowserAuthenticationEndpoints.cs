using System.Security.Claims;
using Lakehold.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Endpoints;

/// <summary>Same-origin browser login, session discovery, and logout.</summary>
public static class BrowserAuthenticationEndpoints
{
    /// <summary>Maps endpoints used by the Workbench OIDC flow.</summary>
    public static IEndpointRouteBuilder MapBrowserAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var auth = app.MapGroup("/auth").WithTags("Authentication");
        auth.MapGet("/session", Session)
            .WithSummary("Reports whether browser OIDC is configured and signed in.");
        auth.MapGet("/login", Login)
            .WithSummary("Starts the Workbench authorization-code flow.");
        auth.MapGet("/logout", Logout)
            .WithSummary("Clears the local Workbench session.");

        return app;
    }

    internal static BrowserSessionDto Session(
        ClaimsPrincipal user,
        IOptions<LakeholdOidcOptions> configured,
        IOptions<LakeholdAuthOptions> auth)
    {
        var options = configured.Value;
        var principal = OidcPrincipal.TryResolve(user, options);
        return new BrowserSessionDto(
            options.BrowserLoginEnabled,
            principal is not null,
            principal is null ? null : DisplayName(user),
            principal?.Scope == Lakehold.ControlPlane.Model.TokenScope.Instance,
            auth.Value.RequireAuthentication);
    }

    internal static IResult Login(string? returnUrl, IOptions<LakeholdOidcOptions> configured)
    {
        if (!configured.Value.BrowserLoginEnabled)
        {
            return Results.NotFound("Browser OIDC login is not configured.");
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) },
            [BrowserAuthentication.OidcScheme]);
    }

    internal static IResult Logout(string? returnUrl, IOptions<LakeholdOidcOptions> configured)
    {
        var redirect = SafeReturnUrl(returnUrl);
        if (!configured.Value.BrowserLoginEnabled)
        {
            return Results.Redirect(redirect);
        }

        return Results.SignOut(
            new AuthenticationProperties { RedirectUri = redirect },
            [BrowserAuthentication.CookieScheme]);
    }

    internal static string SafeReturnUrl(string? returnUrl)
        => returnUrl is { Length: > 0 }
           && returnUrl[0] == '/'
           && !returnUrl.StartsWith("//", StringComparison.Ordinal)
           && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/workbench";

    private static string? DisplayName(ClaimsPrincipal user)
        => user.Identity?.Name
           ?? user.FindFirstValue("name")
           ?? user.FindFirstValue("preferred_username")
           ?? user.FindFirstValue("email");
}

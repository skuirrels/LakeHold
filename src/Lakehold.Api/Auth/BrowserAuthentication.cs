using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Lakehold.ControlPlane.Data;

namespace Lakehold.Api.Auth;

/// <summary>Registers bearer validation and an optional authorization-code browser session.</summary>
public static class BrowserAuthentication
{
    public const string SelectorScheme = "lakehold.authentication";
    public const string CookieScheme = "lakehold.browser";
    public const string JwtScheme = "lakehold.jwt";
    public const string OidcScheme = "lakehold.oidc";

    /// <summary>Adds the configured OIDC resource-server and browser-login surfaces.</summary>
    public static IServiceCollection AddLakeholdAuthentication(
        this IServiceCollection services,
        LakeholdOidcOptions oidc)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(oidc);

        if (!oidc.Enabled)
        {
            return services;
        }

        services
            .AddDataProtection()
            .SetApplicationName("LakeHold")
            .PersistKeysToDbContext<ControlPlaneContext>();

        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = SelectorScheme;
                options.DefaultAuthenticateScheme = SelectorScheme;
            })
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        && !authorization.StartsWith("Bearer lkh_", StringComparison.Ordinal)
                            ? JwtScheme
                            : CookieScheme;
                };
            })
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "LakeHold.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.IsEssential = true;
                // The OIDC handler owns its separate correlation/nonce cookies. The application
                // session itself never needs to ride a cross-site request, so Strict is the
                // appropriate CSRF boundary for browser-admin mutations.
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            })
            .AddJwtBearer(JwtScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.MapInboundClaims = false;
                ConfigureAudience(options, oidc.Audience);
            });

        if (oidc.BrowserLoginEnabled)
        {
            authentication.AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SignInScheme = CookieScheme;
                options.CallbackPath = "/auth/callback";
                options.RemoteSignOutPath = "/auth/remote-logout";
                options.SignedOutCallbackPath = "/auth/signed-out";
                options.MapInboundClaims = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SaveTokens = false;

                foreach (var scope in oidc.Scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)))
                {
                    options.Scope.Add(scope.Trim());
                }
            });
        }

        services.AddAuthorization();
        return services;
    }

    private static void ConfigureAudience(JwtBearerOptions options, string audience)
    {
        if (audience.Length > 0)
        {
            options.Audience = audience;
        }
        else
        {
            options.TokenValidationParameters.ValidateAudience = false;
        }
    }
}

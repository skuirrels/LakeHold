using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Lakehold.Api.Mcp;
using Microsoft.Extensions.Options;

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
                if (oidc.MetadataAddress.Length > 0)
                {
                    options.MetadataAddress = oidc.MetadataAddress;
                }

                ConfigureAudience(options, oidc);
            });

        if (oidc.BrowserLoginEnabled)
        {
            authentication.AddOpenIdConnect(OidcScheme, options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = oidc.ClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                if (oidc.MetadataAddress.Length > 0)
                {
                    options.MetadataAddress = oidc.MetadataAddress;
                }

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

    private static void ConfigureAudience(JwtBearerOptions options, LakeholdOidcOptions oidc)
    {
        // Audience is request-specific: ordinary API and Workbench calls target the configured API
        // audience, while RFC 8707 makes an MCP access token target the MCP resource URI itself.
        // Signature, issuer, lifetime, and every other token check remain owned by JwtBearer.
        options.TokenValidationParameters.ValidateAudience = false;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var expected = oidc.Audience;
                var mcp = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<McpOptions>>()
                    .Value;

                if (context.HttpContext.Request.Path.StartsWithSegments(mcp.Route))
                {
                    var settings = await context.HttpContext.RequestServices
                        .GetRequiredService<McpRuntimeSettingsStore>()
                        .GetAsync(context.HttpContext.RequestAborted)
                        .ConfigureAwait(false);
                    expected = McpResourceMetadata.AbsoluteResourceUri(context.HttpContext, settings);
                }

                if (!LakeholdAudience.Matches(context.Principal!, expected))
                {
                    context.Fail("The access token was not issued for this resource.");
                }
            },
        };
    }
}

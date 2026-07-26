namespace Lakehold.Api.Auth;

/// <summary>Authentication configuration, bound from <c>Lakehold:Auth</c>.</summary>
public sealed class LakeholdAuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Lakehold:Auth";

    /// <summary>
    ///     Whether a request must carry a valid token. Default false: a request with no token falls
    ///     back to trusting the route, preserving today's behaviour until token issuance (phase 1,
    ///     step 3) and the workbench wiring (step 4) land. A token that <em>is</em> presented is always
    ///     validated, regardless of this flag.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>
    ///     Tenant exposed in demo mode to credential-less visitors as a tightly scoped reader.
    ///     Empty disables demo access. <see cref="DemoCatalog"/> must also be set.
    /// </summary>
    public string DemoTenant { get; set; } = string.Empty;

    /// <summary>
    ///     The single catalog exposed inside <see cref="DemoTenant"/>. Empty disables demo access,
    ///     even when a tenant was configured, so an incomplete deployment fails closed.
    /// </summary>
    public string DemoCatalog { get; set; } = string.Empty;
}

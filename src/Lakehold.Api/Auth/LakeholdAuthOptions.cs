namespace Lakehold.Api.Auth;

/// <summary>Authentication configuration, bound from <c>Lakehold:Auth</c>.</summary>
public sealed class LakeholdAuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string Section = "Lakehold:Auth";

    // There is deliberately no RequireAuthentication switch. It existed to keep token-less callers
    // working "until token issuance and the workbench wiring land"; both landed, the switch stayed,
    // and because it defaulted to off the entire authorization layer was inert in the one
    // configuration developers actually ran. Authentication is not optional. An operator who wants
    // to publish something without a credential does it by publishing a demo catalog below, which
    // is a real read-only identity scoped to one tenant rather than a bypass.

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

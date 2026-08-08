using Lakehold.Api.Auth;
using Lakehold.ControlPlane.Security;
using ModelContextProtocol;

namespace Lakehold.Api.Mcp;

/// <summary>
///     How a tool or resource learns who is calling and whether they may. One implementation, so the
///     two surfaces cannot drift — a resource that authorised differently from a tool would be a hole
///     shaped exactly like the one invariant 21 exists to close.
/// </summary>
internal static class McpCaller
{
    internal const string SettingsItemKey = "Lakehold.Mcp.RuntimeSettings";

    /// <summary>The credential this call is acting as, or throws if there somehow is none.</summary>
    /// <remarks>
    ///     <see cref="McpAuthenticationFilter"/> refuses a credential-less caller before any tool or
    ///     resource runs, so an authenticated principal holds on every reachable path. Asserting it
    ///     again here means a future transport cannot quietly bypass that filter.
    /// </remarks>
    public static ILakeholdPrincipal Principal(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        var http = accessor.HttpContext
            ?? throw new McpException("This server is only available over the HTTP transport.");

        // The MCP filter refuses a credential-less call before this runs, and GetLakeholdPrincipal
        // throws if no filter resolved one, so there is no unauthenticated case left to guard.
        return http.GetLakeholdPrincipal();
    }

    /// <summary>The runtime settings snapshot loaded before this transport request was accepted.</summary>
    public static McpRuntimeSettings Settings(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);

        var http = accessor.HttpContext
            ?? throw new McpException("This server is only available over the HTTP transport.");

        return http.Items.TryGetValue(SettingsItemKey, out var value)
            && value is McpRuntimeSettings settings
                ? settings
                : throw new McpException("MCP runtime settings were not resolved for this request.");
    }

    /// <summary>Resolves the principal and enforces <see cref="Capability.TenantData"/>, or throws.</summary>
    /// <remarks>
    ///     The refusal wording for an unreachable subject deliberately matches the engine's own
    ///     "not found" message. A caller must not be able to tell "you may not reach this tenant"
    ///     from "this catalog does not exist", because the difference would answer whether the tenant
    ///     exists (invariant 19).
    /// </remarks>
    public static ILakeholdPrincipal Authorize(IHttpContextAccessor accessor, string tenant, string catalog)
    {
        var principal = Principal(accessor);
        var decision = CapabilityPolicy.Evaluate(principal, Capability.TenantData, tenant, catalog);

        return decision.Outcome switch
        {
            CapabilityOutcome.Allowed => principal,
            CapabilityOutcome.Forbidden => throw new McpException(decision.Detail ?? "Forbidden."),
            _ => throw new McpException($"Catalog '{catalog}' was not found for tenant '{tenant}'."),
        };
    }

    /// <summary>Resolves the principal and enforces the administrator capability for connector control-plane work.</summary>
    public static ILakeholdPrincipal AuthorizeOwner(IHttpContextAccessor accessor, string tenant, string catalog)
    {
        var principal = Principal(accessor);
        var decision = CapabilityPolicy.Evaluate(principal, Capability.TenantOwner, tenant, catalog);
        return decision.Outcome switch
        {
            CapabilityOutcome.Allowed => principal,
            CapabilityOutcome.Forbidden => throw new McpException(decision.Detail ?? "Forbidden."),
            _ => throw new McpException($"Catalog '{catalog}' was not found for tenant '{tenant}'."),
        };
    }
}

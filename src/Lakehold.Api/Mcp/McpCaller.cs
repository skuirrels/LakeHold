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
        => AuthorizeCapability(accessor, Capability.TenantData, tenant, catalog);

    /// <summary>Resolves the principal and enforces the administrator capability for connector control-plane work.</summary>
    public static ILakeholdPrincipal AuthorizeOwner(IHttpContextAccessor accessor, string tenant, string catalog)
        => AuthorizeCapability(accessor, Capability.TenantOwner, tenant, catalog);

    /// <summary>
    ///     The administrator capability plus the operator's write opt-in, for a tool that changes
    ///     durable state or moves tenant data.
    /// </summary>
    /// <remarks>
    ///     The request filters already refuse a mutating tool while writes are disabled. Keeping the
    ///     check on the tool as well means a future transport or SDK dispatch that misses the filter
    ///     still fails closed, exactly as <see cref="LakeholdWriteTools"/> does for `execute`.
    /// </remarks>
    public static ILakeholdPrincipal AuthorizeOwnerForWrite(
        IHttpContextAccessor accessor,
        string tenant,
        string catalog)
    {
        var principal = AuthorizeOwner(accessor, tenant, catalog);
        if (!Settings(accessor).AllowWrites)
        {
            throw new McpException("Writing through MCP is disabled in LakeHold System Settings.");
        }

        return principal;
    }

    /// <summary>Enforces ordinary tenant-write capability plus the shared MCP write opt-in.</summary>
    public static ILakeholdPrincipal AuthorizeForWrite(
        IHttpContextAccessor accessor,
        string tenant,
        string catalog)
    {
        var principal = AuthorizeCapability(accessor, Capability.TenantWrite, tenant, catalog);

        if (!Settings(accessor).AllowWrites)
        {
            throw new McpException("Writing through MCP is disabled in LakeHold System Settings.");
        }

        return principal;
    }

    private static ILakeholdPrincipal AuthorizeCapability(
        IHttpContextAccessor accessor,
        Capability capability,
        string tenant,
        string catalog)
    {
        var principal = Principal(accessor);
        var decision = CapabilityPolicy.Evaluate(principal, capability, tenant, catalog);
        return decision.Outcome switch
        {
            CapabilityOutcome.Allowed => principal,
            CapabilityOutcome.Forbidden => throw new McpException(decision.Detail ?? "Forbidden."),
            _ => throw new McpException($"Catalog '{catalog}' was not found for tenant '{tenant}'."),
        };
    }

    /// <summary>Enforces the explicit operator-command tier on top of tenant-owner access.</summary>
    public static ILakeholdPrincipal AuthorizeOperator(
        IHttpContextAccessor accessor,
        string tenant,
        string catalog,
        bool requireWrites)
    {
        var principal = AuthorizeOwner(accessor, tenant, catalog);
        var settings = Settings(accessor);
        if (!settings.AllowOperatorCommands)
        {
            throw new McpException("Operator commands are disabled in LakeHold System Settings.");
        }

        if (requireWrites && !settings.AllowWrites)
        {
            throw new McpException("Writing through MCP is disabled in LakeHold System Settings.");
        }

        return principal;
    }
}

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
}

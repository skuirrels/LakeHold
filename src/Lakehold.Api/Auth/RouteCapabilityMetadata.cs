using Lakehold.ControlPlane.Security;

namespace Lakehold.Api.Auth;

/// <summary>Endpoint metadata declaring a route's <see cref="Capability"/>.</summary>
/// <remarks>
///     How the HTTP transport carries a capability. The rules that read it are in
///     <see cref="CapabilityPolicy"/>, which knows nothing about endpoints — another transport
///     declares its capability its own way and reaches the same rules.
/// </remarks>
public sealed record RouteCapabilityMetadata(Capability Capability);

/// <summary>Extensions for reading the resolved principal off the request.</summary>
public static class PrincipalHttpExtensions
{
    /// <summary>
    ///     The principal the authorization filter resolved for this request, or
    ///     <see cref="LakeholdPrincipal.Legacy"/> when none was stashed — a route reached without the
    ///     filter, or a token-less request while enforcement is not required.
    /// </summary>
    public static ILakeholdPrincipal GetLakeholdPrincipal(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(LakeholdAuthorizationFilter.PrincipalItemKey, out var value)
            && value is ILakeholdPrincipal principal
            ? principal
            : LakeholdPrincipal.Legacy;
    }

    /// <summary>Attaches a <see cref="Capability"/> to an endpoint, read back by the filter.</summary>
    public static TBuilder RequireCapability<TBuilder>(this TBuilder builder, Capability capability)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new RouteCapabilityMetadata(capability));
        return builder;
    }
}

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
    ///     The principal the authorization filter resolved for this request.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The route did not run <see cref="LakeholdAuthorizationFilter"/>. This used to return a
    ///     route-trusting principal instead, which turned "this endpoint forgot its filter" into
    ///     "this endpoint serves anyone" — silently, and only on the endpoint that made the mistake.
    ///     Failing loudly is the point: it is a wiring bug, and it should be impossible to ship.
    /// </exception>
    public static ILakeholdPrincipal GetLakeholdPrincipal(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(LakeholdAuthorizationFilter.PrincipalItemKey, out var value)
            && value is ILakeholdPrincipal principal
            ? principal
            : throw new InvalidOperationException(
                $"No principal was resolved for {http.Request.Path}. Every route that reads a "
                + "principal must have AddEndpointFilter<LakeholdAuthorizationFilter>() applied.");
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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Lakehold.Api.Mcp;

/// <summary>
///     A per-credential request ceiling on the MCP endpoint.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two chained limiters, because one key cannot do both jobs.</b> Partitioning by
///         credential is right for real agents — several run behind one NAT or one reverse proxy and
///         would otherwise share a budget, while one that reconnects would get a fresh one. But the
///         credential is caller-supplied, so partitioning on it alone means a caller sending a fresh
///         random bearer on every request lands in a new partition each time and is never limited:
///         unlimited traffic, and one rate-limiter partition per value. That defeats the reason this
///         runs ahead of authentication, which is to shed bad credentials before they each cost a
///         token lookup.
///     </para>
///     <para>
///         So a per-remote-address limiter runs first and bounds what any one peer can send whatever
///         credentials it claims, and the per-credential limiter runs behind it and gives each real
///         agent its own budget. The address ceiling is deliberately the looser of the two, since a
///         shared egress IP is a normal deployment and must not throttle a whole office to one agent's
///         allowance.
///     </para>
///     <para>
///         The credential partition key is a hash of the <c>Authorization</c> header, never the header
///         itself. The limiter runs before the endpoint filter has resolved the credential to a
///         principal, so the raw header is all there is to key on — and holding a bearer token as a
///         live dictionary key for the window's duration is exactly the kind of incidental credential
///         storage the rest of this code avoids. A hash partitions identically and is not a credential.
///     </para>
/// </remarks>
internal static class McpRateLimiter
{
    /// <summary>Name of the rate-limiting policy applied to the MCP endpoint group.</summary>
    public const string PolicyName = "lakehold-mcp";

    /// <summary>Registers the policy. Applying it is <see cref="McpExtensions.MapLakeholdMcp"/>'s job.</summary>
    public static IServiceCollection AddLakeholdMcpRateLimiter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(limiter =>
        {
            // The endpoint policy: one budget per credential, applied only where the MCP group asks
            // for it by name.
            limiter.AddPolicy(PolicyName, context => Limiter(Credential(context), Permits(context)));

            // The peer ceiling. It lives on the global limiter because the middleware applies that in
            // addition to the endpoint's policy, which is the only way to get two ceilings on one
            // request — an endpoint can carry just one named policy. It returns no limiter for
            // anything outside the MCP route, so it costs a path comparison and nothing else for the
            // rest of the API.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsMcpRequest(context)
                    ? Limiter(Address(context), Permits(context) * AddressMultiplier)
                    : RateLimitPartition.GetNoLimiter("not-mcp"));

            limiter.OnRejected = static (context, cancellationToken) =>
            {
                // Retry-After so a client backs off by the protocol's own means rather than guessing.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    ///     How much looser the per-address ceiling is than the per-credential one.
    /// </summary>
    /// <remarks>
    ///     A shared egress address is ordinary — several agents, or a whole office behind one NAT —
    ///     so this has to admit more than one agent's worth of traffic while still bounding a single
    ///     peer that is cycling credentials.
    /// </remarks>
    private const int AddressMultiplier = 8;

    private static int Permits(HttpContext context)
        => context.RequestServices.GetRequiredService<IOptions<McpOptions>>().Value
            .RequestsPerMinutePerCredential;

    private static bool IsMcpRequest(HttpContext context)
        => context.Request.Path.StartsWithSegments(
            context.RequestServices.GetRequiredService<IOptions<McpOptions>>().Value.Route,
            StringComparison.OrdinalIgnoreCase);

    private static RateLimitPartition<string> Limiter(string key, int permits)
        => permits <= 0
            ? RateLimitPartition.GetNoLimiter("disabled")
            : RateLimitPartition.GetFixedWindowLimiter(
                key,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = TimeSpan.FromMinutes(1),

                    // No queue. Holding an over-budget agent's request open would convert a
                    // refusal it can back off from into latency it cannot see, and the
                    // client's own timeout would fire instead of a 429 it could read.
                    QueueLimit = 0,
                });

    /// <summary>The credential partition: a hash of the bearer, never the bearer.</summary>
    private static string Credential(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authorization))
        {
            return "credential:anonymous";
        }

        return "credential:" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)));
    }

    /// <summary>
    ///     The peer partition. Unlike the credential, this is not something the caller can vary.
    /// </summary>
    /// <remarks>
    ///     Deliberately the connection's remote address and not a forwarded header: <c>X-Forwarded-For</c>
    ///     is caller-supplied unless the proxy list is pinned, which would put the key back under the
    ///     caller's control and reintroduce exactly the bypass this limiter exists to close. Behind a
    ///     reverse proxy every request therefore shares the proxy's address, which is why this ceiling
    ///     is the looser of the two rather than the operative one.
    /// </remarks>
    private static string Address(HttpContext context)
        => "peer:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
}

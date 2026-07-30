using System.Net;
using System.Net.Sockets;

namespace Lakehold.Api.Cdc;

/// <summary>Validates a webhook URI at creation and again immediately before every delivery.</summary>
public static class WebhookDestinationPolicy
{
    /// <summary>Returns a client-safe validation error, or null when the destination is allowed.</summary>
    public static async Task<string?> ValidateAsync(
        Uri endpoint,
        CdcOptions options,
        CancellationToken cancellationToken)
        => (await ResolveAsync(endpoint, options, cancellationToken).ConfigureAwait(false)).Error;

    /// <summary>
    ///     Validates a destination and returns the public address that the HTTP connection must use.
    ///     Returning the resolved address closes the DNS-rebinding gap between policy evaluation and
    ///     socket connection.
    /// </summary>
    internal static async Task<WebhookDestinationResolution> ResolveAsync(
        Uri endpoint,
        CdcOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        if (endpoint.Scheme != Uri.UriSchemeHttps
            && !(options.AllowHttp && endpoint.Scheme == Uri.UriSchemeHttp))
        {
            return WebhookDestinationResolution.Refused(
                options.AllowHttp
                    ? "An absolute http or https endpoint URL is required."
                    : "An absolute https endpoint URL is required.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return WebhookDestinationResolution.Refused(
                "Webhook endpoint URLs must not contain embedded credentials.");
        }

        if (!HostAllowed(endpoint.DnsSafeHost, options.AllowedHosts))
        {
            return WebhookDestinationResolution.Refused(
                "The webhook endpoint host is not allowed by the operator's CDC egress policy.");
        }

        if (options.AllowUnsafeDestinations)
        {
            return WebhookDestinationResolution.Allowed(address: null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return WebhookDestinationResolution.Refused(
                "The webhook endpoint host could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return WebhookDestinationResolution.Refused(
                "The webhook endpoint host did not resolve to an address.");
        }

        if (addresses.Any(IsProhibited))
        {
            return WebhookDestinationResolution.Refused(
                "The webhook endpoint resolves to a loopback, private, link-local, multicast, or otherwise prohibited address.");
        }

        // Pin one address from this approved resolution. The connection handler consumes this exact
        // value and does not resolve the hostname a second time.
        return WebhookDestinationResolution.Allowed(addresses[0]);
    }

    private static bool HostAllowed(string host, string[] allowedHosts)
    {
        if (allowedHosts.Length == 0)
        {
            return true;
        }

        foreach (var allowed in allowedHosts)
        {
            if (allowed.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = allowed[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && host.Length > suffix.Length)
                {
                    return true;
                }
            }
            else if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsProhibited(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10 or 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || bytes[0] >= 224;
        }

        // fc00::/7 is IPv6 unique-local space.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}

internal sealed record WebhookDestinationResolution(string? Error, IPAddress? Address)
{
    public static WebhookDestinationResolution Allowed(IPAddress? address) => new(null, address);

    public static WebhookDestinationResolution Refused(string error) => new(error, null);
}

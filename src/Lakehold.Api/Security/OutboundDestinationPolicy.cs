using System.Net;
using System.Net.Sockets;

namespace Lakehold.Api.Security;

/// <summary>Common operator policy for any server-side outbound integration.</summary>
public interface IOutboundDestinationOptions
{
    bool AllowHttp { get; }

    bool AllowUnsafeDestinations { get; }

    string[] AllowedHosts { get; }
}

/// <summary>
///     Resolves and validates an outbound URI, rejecting credential-bearing URLs and non-public
///     addresses unless a development deployment explicitly opts out.
/// </summary>
public static class OutboundDestinationPolicy
{
    public static async Task<OutboundDestinationResolution> ResolveAsync(
        Uri endpoint,
        IOutboundDestinationOptions options,
        string integrationName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);

        if (endpoint.Scheme != Uri.UriSchemeHttps
            && !(options.AllowHttp && endpoint.Scheme == Uri.UriSchemeHttp))
        {
            return OutboundDestinationResolution.Refused(
                options.AllowHttp
                    ? "An absolute http or https endpoint URL is required."
                    : "An absolute https endpoint URL is required.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return OutboundDestinationResolution.Refused(
                $"{integrationName} endpoint URLs must not contain embedded credentials.");
        }

        if (!HostAllowed(endpoint.DnsSafeHost, options.AllowedHosts))
        {
            return OutboundDestinationResolution.Refused(
                $"The {integrationName.ToLowerInvariant()} endpoint host is not allowed by the operator's egress policy.");
        }

        if (options.AllowUnsafeDestinations)
        {
            return OutboundDestinationResolution.Allowed(address: null);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(endpoint.DnsSafeHost, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return OutboundDestinationResolution.Refused(
                $"The {integrationName.ToLowerInvariant()} endpoint host could not be resolved.");
        }

        if (addresses.Length == 0)
        {
            return OutboundDestinationResolution.Refused(
                $"The {integrationName.ToLowerInvariant()} endpoint host did not resolve to an address.");
        }

        if (addresses.Any(IsProhibited))
        {
            return OutboundDestinationResolution.Refused(
                $"The {integrationName.ToLowerInvariant()} endpoint resolves to a loopback, private, link-local, multicast, or otherwise prohibited address.");
        }

        return OutboundDestinationResolution.Allowed(addresses[0]);
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
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
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

        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xfe) == 0xfc;
    }
}

public sealed record OutboundDestinationResolution(string? Error, IPAddress? Address)
{
    public static OutboundDestinationResolution Allowed(IPAddress? address) => new(null, address);

    public static OutboundDestinationResolution Refused(string error) => new(error, null);
}

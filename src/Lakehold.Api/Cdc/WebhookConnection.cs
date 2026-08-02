using System.Net;
using Lakehold.Api.Security;

namespace Lakehold.Api.Cdc;

/// <summary>
///     Creates webhook connections that use the address approved by
///     <see cref="WebhookDestinationPolicy"/> while retaining the original hostname for HTTP and TLS.
/// </summary>
internal static class WebhookConnection
{
    internal static readonly HttpRequestOptionsKey<IPAddress> ApprovedAddress =
        OutboundConnection.ApprovedAddress;

    public static SocketsHttpHandler CreateHandler() => OutboundConnection.CreateHandler();
}

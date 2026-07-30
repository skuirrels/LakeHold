using System.Net;
using System.Net.Sockets;

namespace Lakehold.Api.Cdc;

/// <summary>
///     Creates webhook connections that use the address approved by
///     <see cref="WebhookDestinationPolicy"/> while retaining the original hostname for HTTP and TLS.
/// </summary>
internal static class WebhookConnection
{
    internal static readonly HttpRequestOptionsKey<IPAddress> ApprovedAddress =
        new("Lakehold.Cdc.ApprovedAddress");

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        // A system proxy would make the callback's endpoint the proxy while the approved address
        // belongs to the webhook host, breaking the validation-to-connection binding.
        UseProxy = false,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var socket = context.InitialRequestMessage.Options.TryGetValue(ApprovedAddress, out var address)
            ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);
        socket.NoDelay = true;

        try
        {
            EndPoint endpoint = address is null
                ? context.DnsEndPoint
                : new IPEndPoint(address, context.DnsEndPoint.Port);
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

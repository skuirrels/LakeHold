using System.Net;
using System.Net.Sockets;

namespace Lakehold.Api.Security;

/// <summary>Creates HTTP connections pinned to the address approved by outbound policy.</summary>
internal static class OutboundConnection
{
    internal static readonly HttpRequestOptionsKey<IPAddress> ApprovedAddress =
        new("Lakehold.Outbound.ApprovedAddress");

    public static SocketsHttpHandler CreateHandler() => CreateHandler(address: null);

    public static SocketsHttpHandler CreateHandler(IPAddress? address) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context, address, cancellationToken),
    };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        IPAddress? fixedAddress,
        CancellationToken cancellationToken)
    {
        var address = fixedAddress;
        if (address is null && context.InitialRequestMessage.Options.TryGetValue(ApprovedAddress, out var requested))
        {
            address = requested;
        }

        var socket = address is null
            ? new Socket(SocketType.Stream, ProtocolType.Tcp)
            : new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
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

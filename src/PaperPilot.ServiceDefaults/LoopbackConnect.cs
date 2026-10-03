using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Connects to <c>localhost</c> over IPv4 first. Windows resolves <c>localhost</c> to <c>::1</c> before
/// <c>127.0.0.1</c>, and .NET tries the addresses one after another. Container ports (OpenSearch, Redis) and Ollama
/// listen on <c>127.0.0.1</c> only, and on Windows a refused connection takes about 2 seconds, so every new pooled
/// connection paid 2 seconds before reaching the service. The parity check found it as slow retrievals.
/// </summary>
public static class LoopbackConnect
{
    private static readonly IPAddress[] LoopbackOrder = [IPAddress.Loopback, IPAddress.IPv6Loopback];

    /// <summary>A <see cref="SocketsHttpHandler.ConnectCallback"/>: IPv4 then IPv6 for localhost, the default otherwise.</summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;
        if (!string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return await ConnectAsync(new Socket(SocketType.Stream, ProtocolType.Tcp), endpoint, cancellationToken);
        }

        for (var i = 0; ; i++)
        {
            var address = LoopbackOrder[i];
            try
            {
                return await ConnectAsync(
                    new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp),
                    new IPEndPoint(address, endpoint.Port),
                    cancellationToken);
            }
            catch (SocketException) when (i < LoopbackOrder.Length - 1)
            {
                // Nothing listens on this address; try the next one.
            }
        }
    }

    private static async ValueTask<Stream> ConnectAsync(Socket socket, EndPoint endpoint, CancellationToken cancellationToken)
    {
        socket.NoDelay = true;
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

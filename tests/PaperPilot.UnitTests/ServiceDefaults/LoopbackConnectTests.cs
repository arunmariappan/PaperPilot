using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PaperPilot.UnitTests.ServiceDefaults;

/// <summary>
/// Container ports and Ollama listen on 127.0.0.1 only. On Windows, trying ::1 first cost ~2 s per new connection
/// (found by the parity check), so default clients connect to localhost over IPv4 first.
/// </summary>
public sealed class LoopbackConnectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_default_client_reaches_an_ipv4_only_localhost_port_at_once()
    {
        using var server = new OneResponseServer(IPAddress.Loopback);
        using var host = SlowEndpoint.BuildHost(services => services.AddHttpClient("default"));
        var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("default");

        var stopwatch = Stopwatch.StartNew();
        var body = await client.GetStringAsync(new Uri($"http://localhost:{server.Port}/"), Ct);

        body.ShouldBe("ok");
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task It_falls_back_to_ipv6_when_only_that_listens()
    {
        Assert.SkipUnless(Socket.OSSupportsIPv6, "IPv6 is not available.");
        using var server = new OneResponseServer(IPAddress.IPv6Loopback);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = LoopbackConnect.ConnectAsync });

        (await http.GetStringAsync(new Uri($"http://localhost:{server.Port}/"), Ct)).ShouldBe("ok");
    }

    [Fact]
    public async Task Other_hosts_connect_as_usual()
    {
        using var server = new OneResponseServer(IPAddress.Loopback);
        using var http = new HttpClient(new SocketsHttpHandler { ConnectCallback = LoopbackConnect.ConnectAsync });

        (await http.GetStringAsync(new Uri($"http://127.0.0.1:{server.Port}/"), Ct)).ShouldBe("ok");
    }

    /// <summary>Answers one HTTP request with <c>ok</c>, listening on a single address.</summary>
    private sealed class OneResponseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serving;

        public OneResponseServer(IPAddress address)
        {
            _listener = new TcpListener(address, 0);
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose()
        {
            _listener.Stop();
            _listener.Dispose();
            _ = _serving.ContinueWith(_ => { }, TaskScheduler.Default);
        }

        private async Task ServeAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        }
    }
}

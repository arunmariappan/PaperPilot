using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.ServiceDefaults;

/// <summary>
/// R1: AddServiceDefaults puts the standard resilience handler (10 s per attempt, ~30 s in total) on every HttpClient.
/// This endpoint answers after 12 s, so a default client gives up on it.
/// </summary>
public sealed class SlowEndpoint : IDisposable
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(12);

    private readonly WireMockServer _server = WireMockServer.Start();

    public SlowEndpoint() =>
        _server.Given(Request.Create().WithPath("/slow").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("done").WithDelay(Delay));

    public Uri Url => new($"{_server.Url}/slow");

    /// <summary>A host configured exactly like the PaperPilot services, plus the given client registrations.</summary>
    public static IHost BuildHost(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        configure(builder.Services);
        return builder.Build();
    }

    public void Dispose() => _server.Dispose();
}

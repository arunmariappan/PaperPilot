using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly.Timeout;

namespace PaperPilot.UnitTests.ServiceDefaults;

// A separate class from LongRunningResilienceTests so the two slow tests run in parallel.
public sealed class StandardResilienceTests : IDisposable
{
    private readonly SlowEndpoint _endpoint = new();
    private readonly IHost _host = SlowEndpoint.BuildHost(services => services.AddHttpClient("default"));

    [Fact]
    public async Task Default_client_gives_up_on_a_slow_response()
    {
        var client = _host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("default");

        await Should.ThrowAsync<TimeoutRejectedException>(
            () => client.GetStringAsync(_endpoint.Url, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        _host.Dispose();
        _endpoint.Dispose();
    }
}

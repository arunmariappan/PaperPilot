using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PaperPilot.UnitTests.ServiceDefaults;

public sealed class LongRunningResilienceTests : IDisposable
{
    private readonly SlowEndpoint _endpoint = new();
    private readonly IHost _host = SlowEndpoint.BuildHost(services => services
        .AddHttpClient("long-running")
        .AddLongRunningResilienceHandler("long-running", TimeSpan.FromMinutes(5)));

    [Fact]
    public async Task Waits_for_a_response_slower_than_the_standard_handler_allows()
    {
        var client = _host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("long-running");

        var body = await client.GetStringAsync(_endpoint.Url, TestContext.Current.CancellationToken);

        body.ShouldBe("done");
    }

    [Fact]
    public void Leaves_the_limit_to_the_pipeline_instead_of_HttpClient_Timeout()
    {
        var client = _host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("long-running");

        client.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _host.Dispose();
        _endpoint.Dispose();
    }
}

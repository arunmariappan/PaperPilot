using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

namespace PaperPilot.SmokeTests;

/// <summary>
/// Boots the whole AppHost and checks that the API answers <c>/api/v1/health</c> with its database and search index
/// reachable. It pulls every image on a fresh machine (docling-serve alone is several GB), so it runs only on demand:
/// the manual "Smoke test" workflow, or locally with the dev stack stopped (its persistent containers have the same
/// names and ports).
/// </summary>
public sealed class AppHostSmokeTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(20);

    [Fact]
    public async Task The_api_starts_and_reaches_postgres_and_opensearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.PaperPilot_AppHost>(
        [
            // Starting needs a value but no real key: nothing is embedded.
            "Parameters:jina-api-key=smoke-test",
            // Whatever the AppHost's user secrets say, a test never polls the Telegram bot or starts Langfuse.
            "Parameters:telegram-bot-token=",
            "Langfuse:Enabled=false",
        ], ct);

        await using var app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", ct).WaitAsync(StartupTimeout, ct);

        using var http = app.CreateHttpClient("api");
        var health = await http.GetFromJsonAsync<JsonObject>("/api/v1/health", ct);

        var services = health.ShouldNotBeNull()["services"].ShouldNotBeNull();
        services["database"]?["status"]?.GetValue<string>().ShouldBe("healthy");
        services["opensearch"]?["status"]?.GetValue<string>().ShouldBe("healthy");
    }
}

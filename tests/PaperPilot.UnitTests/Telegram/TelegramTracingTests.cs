using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using PaperPilot.Api.Telegram;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Telegram;

/// <summary>The bot token is part of every Bot API URL, so HTTP client spans must never carry it.</summary>
public sealed class TelegramTracingTests : IDisposable
{
    private const string Token = "123456:secret-token_value";

    private readonly WireMockServer _telegram = WireMockServer.Start();

    [Fact]
    public async Task Polls_are_not_traced_and_other_calls_hide_the_token()
    {
        _telegram.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithBody("""{"ok":true,"result":[]}"""));
        var exported = new List<Activity>();
        using (var tracing = Sdk.CreateTracerProviderBuilder()
            .AddHttpClientInstrumentation(TelegramRegistration.HideBotToken)
            .AddProcessor(new Capture(exported))
            .Build())
        using (var http = new HttpClient())
        {
            var ct = TestContext.Current.CancellationToken;
            (await http.PostAsync($"{_telegram.Url}/bot{Token}/getUpdates", null, ct)).EnsureSuccessStatusCode();
            (await http.PostAsync($"{_telegram.Url}/bot{Token}/sendMessage?chat_id=42", null, ct)).EnsureSuccessStatusCode();
            (await http.PostAsync($"{_telegram.Url}/other/path", null, ct)).EnsureSuccessStatusCode();
        }

        // The tracer listens process-wide, so keep only this test's requests.
        var ours = exported.Where(a => a.GetTagItem("url.full") is string url && url.StartsWith(_telegram.Url!, StringComparison.Ordinal)).ToList();
        ours.Select(a => (string?)a.GetTagItem("url.full")).ShouldBe(
            [$"{_telegram.Url}/bot{{token}}/sendMessage", $"{_telegram.Url}/other/path"]);
        exported.SelectMany(a => a.TagObjects).Select(t => $"{t.Value}").ShouldAllBe(v => !v.Contains("secret-token"));
    }

    public void Dispose() => _telegram.Dispose();

    private sealed class Capture(List<Activity> exported) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            lock (exported)
            {
                exported.Add(data);
            }
        }
    }
}

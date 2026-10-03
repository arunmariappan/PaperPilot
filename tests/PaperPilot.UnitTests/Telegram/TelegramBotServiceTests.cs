using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using PaperPilot.Api.Telegram;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Retrieval;
using PaperPilot.UnitTests.Rag;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Telegram;

/// <summary>The hosted service: when it runs, and its polling loop against a fake Bot API (WireMock).</summary>
public sealed class TelegramBotServiceTests : IDisposable
{
    private const string Token = "123456:test-token";

    private readonly WireMockServer _telegram = WireMockServer.Start();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("false", Token)]
    [InlineData("true", "")]
    public async Task Without_enabled_and_a_token_the_bot_is_skipped(string enabled, string token)
    {
        using var host = Host(new() { ["Telegram:Enabled"] = enabled, ["Telegram:BotToken"] = token });

        await RunAsync(host);

        Logs(host).ShouldContain("Telegram bot not configured - skipping initialization");
        _telegram.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failure_to_start_is_logged_and_does_not_stop_the_api()
    {
        _telegram.Given(Request.Create().WithPath($"/bot{Token}/getMe"))
            .RespondWith(Response.Create().WithStatusCode(401).WithBodyAsJson(new { ok = false, error_code = 401, description = "Unauthorized" }));
        using var host = Host(Enabled());

        await RunAsync(host);

        Logs(host).ShouldContain(l => l.StartsWith("Failed to start Telegram bot: Unauthorized", StringComparison.Ordinal));
        _telegram.LogEntries.Select(e => e.RequestMessage!.Path).ShouldBe([$"/bot{Token}/getMe"]);
    }

    [Fact]
    public async Task It_removes_the_webhook_polls_for_messages_and_answers_them()
    {
        StubBotApi();
        using var host = Host(Enabled());

        await host.StartAsync(Ct);
        await WaitUntil(() => Calls("sendMessage").Count > 0);
        await host.StopAsync(Ct);

        var deleteWebhook = Calls("deleteWebhook").ShouldHaveSingleItem();
        (deleteWebhook["drop_pending_updates"]?.GetValue<bool>() ?? false).ShouldBeFalse(); // pending updates are kept
        Calls("getUpdates")[0]["allowed_updates"]!.AsArray().Select(u => u!.GetValue<string>()).ShouldBe(["message"]);
        var reply = Calls("sendMessage").ShouldHaveSingleItem();
        (reply["chat_id"]!.GetValue<long>(), reply["text"]!.GetValue<string>()).ShouldBe((42, TelegramMessages.Help));
        reply["link_preview_options"]!["is_disabled"]!.GetValue<bool>().ShouldBeTrue();
        Logs(host).ShouldContain("Telegram bot started successfully (@PaperPilotBot)");
        Logs(host).ShouldContain("Telegram bot stopped");
    }

    public void Dispose() => _telegram.Dispose();

    private Dictionary<string, string?> Enabled() => new()
    {
        ["Telegram:Enabled"] = "true",
        ["Telegram:BotToken"] = Token,
        ["Telegram:BaseUrl"] = _telegram.Url,
    };

    /// <summary>getMe, deleteWebhook, one <c>/help</c> update and then none, and sendMessage.</summary>
    private void StubBotApi()
    {
        Ok("getMe", new JsonObject { ["id"] = 1, ["is_bot"] = true, ["first_name"] = "PaperPilot", ["username"] = "PaperPilotBot" });
        Ok("deleteWebhook", true);
        Ok("sendMessage", Message("/help"));
        _telegram.Given(Request.Create().WithPath($"/bot{Token}/getUpdates"))
            .RespondWith(Response.Create().WithDelay(TimeSpan.FromMilliseconds(100)).WithCallback(request =>
            {
                var offset = JsonNode.Parse(request.Body ?? "{}")?["offset"]?.GetValue<int>() ?? 0;
                var updates = offset > 1 ? new JsonArray() : new JsonArray(new JsonObject { ["update_id"] = 1, ["message"] = Message("/help") });
                return new WireMock.ResponseMessage
                {
                    StatusCode = 200,
                    BodyData = new WireMock.Util.BodyData
                    {
                        BodyAsString = new JsonObject { ["ok"] = true, ["result"] = updates }.ToJsonString(),
                        DetectedBodyType = WireMock.Types.BodyType.String,
                    },
                };
            }));
    }

    private void Ok(string method, JsonNode result) =>
        _telegram.Given(Request.Create().WithPath($"/bot{Token}/{method}"))
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json")
                .WithBody(new JsonObject { ["ok"] = true, ["result"] = result }.ToJsonString()));

    private static JsonObject Message(string text) => new()
    {
        ["message_id"] = 10,
        ["date"] = 1_790_000_000,
        ["chat"] = new JsonObject { ["id"] = 42, ["type"] = "private" },
        ["text"] = text,
        ["entities"] = new JsonArray(new JsonObject { ["type"] = "bot_command", ["offset"] = 0, ["length"] = text.Length }),
    };

    private List<JsonNode> Calls(string method) =>
        [.. _telegram.LogEntries.Where(e => e.RequestMessage!.Path == $"/bot{Token}/{method}")
            .Select(e => JsonNode.Parse(e.RequestMessage!.Body ?? "{}")!)];

    private static async Task RunAsync(IHost host)
    {
        await host.StartAsync(Ct);
        var service = host.Services.GetServices<IHostedService>().OfType<TelegramBotService>().Single();
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.StopAsync(Ct);
    }

    private static List<string> Logs(IHost host) =>
        [.. host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot()
            .Where(r => r.Category == typeof(TelegramBotService).FullName).Select(r => r.Message)];

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>The bot with its real registration; RAG gets fakes, since only <c>/help</c> is sent here.</summary>
    private static IHost Host(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddFakeLogging(o => o.CollectRecordsForDisabledLogLevels = false);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.AddPaperPilotOptions();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IChatClient, FakeChatClient>();
        builder.Services.AddSingleton<IPaperRetriever, FakeRetriever>();
        builder.Services.AddSingleton<IAnswerCache, InMemoryAnswerCache>();
        builder.Services.AddSingleton<ChatOptionsFactory>();
        builder.Services.AddSingleton<RagService>();
        builder.Services.AddSingleton<IAgenticRagService, UnusedAgent>();
        builder.AddPaperPilotTelegram();
        return builder.Build();
    }

    private sealed class UnusedAgent : IAgenticRagService
    {
        public Task<AgenticAskResponse> AskAsync(AskRequest request, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

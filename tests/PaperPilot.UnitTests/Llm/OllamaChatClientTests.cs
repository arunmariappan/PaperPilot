using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Prompts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Llm;

/// <summary>The registered <see cref="IChatClient"/> against WireMock standing in for Ollama (plan R5).</summary>
public sealed class OllamaChatClientTests : IDisposable
{
    private const string Answer =
        """{"model":"qwen3.5:9b","created_at":"2026-10-02T00:00:00Z","message":{"role":"assistant","content":"An answer."},"done":true,"done_reason":"stop","prompt_eval_count":120,"eval_count":30}""";

    private readonly WireMockServer _ollama = WireMockServer.Start();
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IRequestBuilder ChatRequest => Request.Create().WithPath("/api/chat").UsingPost();

    [Fact]
    public async Task Generation_settings_reach_ollama() // R5: think, temperature and top_p
    {
        _ollama.Given(ChatRequest).RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(Answer));
        var (chat, options) = Services();
        var prompt = RagPromptBuilder.Build("What are transformers?", []);

        var response = await chat.GetResponseAsync(prompt.ToMessages(), options.Create(null, 0.7f, 0.9f), Ct);

        response.Text.ShouldBe("An answer.");
        (response.Usage!.InputTokenCount, response.Usage.OutputTokenCount).ShouldBe((120L, 30L));
        var body = JsonNode.Parse(_ollama.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!)!;
        body["model"]!.GetValue<string>().ShouldBe("qwen3.5:9b");
        body["stream"]!.GetValue<bool>().ShouldBeFalse();
        body["think"]!.GetValue<bool>().ShouldBeFalse();
        // Inside "options": Python sent these at the top level, where Ollama ignores them (B29).
        body["options"]!["temperature"]!.GetValue<double>().ShouldBe(0.7, 1e-6);
        body["options"]!["top_p"]!.GetValue<double>().ShouldBe(0.9, 1e-6);
        body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["system", "user"]);
        body["messages"]![0]!["content"]!.GetValue<string>().ShouldBe(RagPromptBuilder.SystemPrompt);
    }

    [Fact]
    public async Task Think_and_the_default_model_come_from_options()
    {
        _ollama.Given(ChatRequest).RespondWith(Response.Create().WithBody(Answer));
        var (chat, options) = Services(new() { ["Ollama:Think"] = "true", ["Ollama:Model"] = "llama3.2:1b" });

        await chat.GetResponseAsync("hi", options.Create(null, 0f), Ct);

        var body = JsonNode.Parse(_ollama.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!)!;
        body["think"]!.GetValue<bool>().ShouldBeTrue();
        body["model"]!.GetValue<string>().ShouldBe("llama3.2:1b");
    }

    [Fact]
    public async Task Streaming_reads_ollamas_ndjson_token_by_token()
    {
        const string stream = """
            {"model":"qwen3.5:9b","created_at":"2026-10-02T00:00:00Z","message":{"role":"assistant","content":"An "},"done":false}
            {"model":"qwen3.5:9b","created_at":"2026-10-02T00:00:00Z","message":{"role":"assistant","content":"answer."},"done":false}
            {"model":"qwen3.5:9b","created_at":"2026-10-02T00:00:00Z","message":{"role":"assistant","content":""},"done":true,"done_reason":"stop","prompt_eval_count":120,"eval_count":2}

            """;
        _ollama.Given(ChatRequest).RespondWith(Response.Create().WithHeader("Content-Type", "application/x-ndjson").WithBody(stream));
        var (chat, options) = Services();

        var tokens = new List<string>();
        await foreach (var update in chat.GetStreamingResponseAsync("hi", options.Create(null, 0.7f, 0.9f), Ct))
        {
            tokens.Add(update.Text);
        }

        tokens.Where(t => t.Length > 0).ShouldBe(["An ", "answer."]);
        JsonNode.Parse(_ollama.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!)!["stream"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_generation_is_not_retried() // R1: no standard resilience handler
    {
        _ollama.Given(ChatRequest).RespondWith(Response.Create().WithStatusCode(500).WithBody("""{"error":"boom"}"""));
        var (chat, options) = Services();

        await Should.ThrowAsync<Exception>(() => chat.GetResponseAsync("hi", options.Create(null, 0.7f), Ct));

        _ollama.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Generation_times_out_after_Ollama_TimeoutSeconds()
    {
        _ollama.Given(ChatRequest).RespondWith(Response.Create().WithBody(Answer).WithDelay(TimeSpan.FromSeconds(5)));
        var (chat, options) = Services(new() { ["Ollama:TimeoutSeconds"] = "1" });
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<TaskCanceledException>(() => chat.GetResponseAsync("hi", options.Create(null, 0.7f), Ct));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task The_model_catalog_lists_installed_models_by_name() // N1
    {
        _ollama.Given(Request.Create().WithPath("/api/tags").UsingGet()).RespondWith(Response.Create().WithBody(
            """{"models":[{"name":"qwen3.5:9b","model":"qwen3.5:9b","size":1},{"name":"llama3.2:1b","model":"llama3.2:1b","size":1}]}"""));
        var catalog = Host().Services.GetRequiredService<OllamaModelCatalog>();

        (await catalog.ListModelsAsync(Ct)).ShouldBe(["llama3.2:1b", "qwen3.5:9b"]);
        catalog.DefaultModel.ShouldBe("qwen3.5:9b");
    }

    public void Dispose()
    {
        _hosts.ForEach(h => h.Dispose());
        _ollama.Dispose();
    }

    private (IChatClient Chat, ChatOptionsFactory Options) Services(Dictionary<string, string?>? settings = null)
    {
        var host = Host(settings);
        return (host.Services.GetRequiredService<IChatClient>(), host.Services.GetRequiredService<ChatOptionsFactory>());
    }

    private IHost Host(Dictionary<string, string?>? settings = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ollama"] = $"Endpoint={_ollama.Url}",
        });
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services.AddLogging();
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotLlm();
        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }
}

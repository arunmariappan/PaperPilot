using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PaperPilot.IntegrationTests.Caching;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace PaperPilot.IntegrationTests.Api;

/// <summary><c>/ask-agentic</c> end to end, with WireMock as Ollama (a scripted reply per prompt) and Jina.</summary>
[Collection(ContainersCollectionDefinition.Name)]
public sealed class AgenticAskApiTests(PostgresFixture postgres, OpenSearchFixture search, RedisFixture redis) : IAsyncLifetime
{
    private const string Answer = "Agents maximise long-term reward [arXiv:2610.00002v1].";
    private const string OffTopic = "What is 2+2?";

    private readonly ApiHost _api = new(postgres, search, redis);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Question => new { query = "How do agents learn from reward?", top_k = 2 };

    public async ValueTask InitializeAsync()
    {
        await search.Client.EnsureIndexAsync(force: true, Ct);
        await search.Client.EnsureRrfPipelineAsync(cancellationToken: Ct);
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);

        _api.StubOllamaAndJina(SampleChunks.Reward.Embedding);
        _api.StubChat(Reply);
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Ask_agentic_answers_with_sources_reasoning_steps_and_a_trace_id()
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask-agentic", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body.Select(p => p.Key).ShouldBe(
            ["query", "answer", "sources", "chunks_used", "search_mode", "reasoning_steps", "retrieval_attempts", "trace_id"],
            ignoreOrder: true);
        body["answer"]!.GetValue<string>().ShouldBe(Answer);
        body["sources"]!.AsArray().Select(s => s!.GetValue<string>())
            .ShouldBe(["https://arxiv.org/pdf/2610.00002.pdf", "https://arxiv.org/pdf/2610.00001.pdf"]);
        (body["chunks_used"]!.GetValue<int>(), body["search_mode"]!.GetValue<string>(), body["retrieval_attempts"]!.GetValue<int>())
            .ShouldBe((2, "hybrid", 1));
        body["reasoning_steps"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(
        [
            "Validated query scope (score: 85/100)",
            "Retrieved documents (1 attempt(s))",
            "Graded documents (1 relevant)",
            "Generated answer from context",
        ]);
        body["trace_id"]!.GetValue<string>().ShouldMatch("^[0-9a-f]{32}$");

        var chats = _api.Requests("/api/chat").Select(r => JsonNode.Parse(r.Body!)!).ToList();
        chats.Count.ShouldBe(3);
        var guardrail = chats[0];
        guardrail["format"]!["properties"]!.AsObject().Select(p => p.Key).ShouldBe(["score", "reason"]);
        (guardrail["think"]!.GetValue<bool>(), guardrail["options"]!["temperature"]!.GetValue<double>()).ShouldBe((false, 0.0));
        chats[2]["format"].ShouldBeNull(); // the answer is plain text
    }

    [Fact]
    public async Task An_off_topic_question_is_declined_without_searching()
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask-agentic", new { query = OffTopic }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["answer"]!.GetValue<string>().ShouldContain($"Your question: '{OffTopic}'");
        (body["retrieval_attempts"]!.GetValue<int>(), body["sources"]!.AsArray().Count).ShouldBe((0, 0));
        _api.Requests("/v1/embeddings").ShouldBeEmpty();
    }

    [Fact]
    public async Task When_ollama_fails_the_guardrail_falls_back_and_the_question_is_declined()
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/chat").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("""{"error":"out of memory"}"""));

        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask-agentic", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["reasoning_steps"]!.AsArray().Select(s => s!.GetValue<string>())
            .ShouldBe(["Validated query scope (score: 50/100)", "Responded as out of scope"]);
    }

    [Fact]
    public async Task A_search_outage_gets_an_explicit_answer() // B6
    {
        var response = await _api.Client(new() { ["OpenSearch:Host"] = "http://127.0.0.1:1" })
            .PostAsJsonAsync("/api/v1/ask-agentic", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["answer"]!.GetValue<string>().ShouldStartWith("I apologize, but the paper search is unavailable right now");
        body["reasoning_steps"]!.AsArray()[^1]!.GetValue<string>().ShouldBe("Search was unavailable");
    }

    [Theory]
    [InlineData("""{"query": ""}""")]
    [InlineData("""{"query": "   "}""")] // C13; Python answered 422
    public async Task A_blank_question_gets_400(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _api.Client().PostAsync("/api/v1/ask-agentic", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject().Select(e => e.Key).ShouldBe(["query"]);
    }

    /// <summary>Ollama's reply, chosen by the prompt's first line like the agent's four LLM calls.</summary>
    private static string Reply(string prompt) => prompt switch
    {
        _ when prompt.StartsWith("You are a guardrail evaluator", StringComparison.Ordinal) => prompt.Contains($"User Query: {OffTopic}", StringComparison.Ordinal)
            ? """{"score": 10, "reason": "Arithmetic, not research."}"""
            : """{"score": 85, "reason": "About reinforcement learning."}""",
        _ when prompt.StartsWith("You are a grader", StringComparison.Ordinal) => """{"binary_score": "yes", "reasoning": "About rewards."}""",
        _ when prompt.StartsWith("You are a question re-writer", StringComparison.Ordinal) => """{"rewritten_query": "reward learning", "reasoning": "Shorter."}""",
        _ => Answer,
    };
}

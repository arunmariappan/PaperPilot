using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.IntegrationTests.Caching;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace PaperPilot.IntegrationTests.Api;

/// <summary><c>/ask</c> and <c>/stream</c> end to end, with WireMock as Ollama and Jina.</summary>
[Collection(ContainersCollectionDefinition.Name)]
public sealed class AskApiTests(PostgresFixture postgres, OpenSearchFixture search, RedisFixture redis) : IAsyncLifetime
{
    private const string Answer = "Agents maximise long-term reward [arXiv:2610.00002v1].";

    private readonly ApiHost _api = new(postgres, search, redis);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Question => new { query = "reward", top_k = 2 };

    /// <summary>No sample chunk is in cs.CV.</summary>
    private static object NoMatches { get; } = new { query = "reward", categories = new[] { "cs.CV" } };

    public async ValueTask InitializeAsync()
    {
        await search.Client.EnsureIndexAsync(force: true, Ct);
        await search.Client.EnsureRrfPipelineAsync(cancellationToken: Ct);
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);
        await redis.FlushAsync();

        _api.StubOllamaAndJina(SampleChunks.Reward.Embedding);
        _api.StubChat("Agents maximise ", "long-term reward ", "[arXiv:2610.00002v1].");
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Ask_answers_from_the_retrieved_chunks()
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body.Select(p => p.Key).ShouldBe(["query", "answer", "sources", "chunks_used", "search_mode"], ignoreOrder: true);
        body["answer"]!.GetValue<string>().ShouldBe(Answer);
        body["sources"]!.AsArray().Select(s => s!.GetValue<string>())
            .ShouldBe(["https://arxiv.org/pdf/2610.00002.pdf", "https://arxiv.org/pdf/2610.00001.pdf"]);
        (body["chunks_used"]!.GetValue<int>(), body["search_mode"]!.GetValue<string>()).ShouldBe((2, "hybrid"));

        var chat = JsonNode.Parse(_api.Requests("/api/chat").ShouldHaveSingleItem().Body!)!;
        chat["messages"]![1]!["content"]!.GetValue<string>().ShouldStartWith(
            "### Context from Papers:\n\n[1. arXiv:2610.00002v1]\nReinforcement learning agents learn policies");
        (chat["think"]!.GetValue<bool>(), chat["options"]!["temperature"]!.GetValue<double>()).ShouldBe((false, 0.7));
    }

    [Fact]
    public async Task A_repeated_question_is_answered_from_the_cache()
    {
        var client = _api.Client();

        var first = await (await client.PostAsJsonAsync("/api/v1/ask", Question, Ct)).Content.ReadAsStringAsync(Ct);
        var second = await (await client.PostAsJsonAsync("/api/v1/ask", Question, Ct)).Content.ReadAsStringAsync(Ct);

        second.ShouldBe(first);
        _api.Requests("/api/chat").Count().ShouldBe(1);
        redis.AnswerKeys().ShouldBe([CacheKey.Compute(new AskRequest { Query = "reward", TopK = 2 }, "qwen3.5:9b")]);
    }

    [Fact]
    public async Task Without_matching_chunks_the_canned_answer_is_returned_and_not_cached()
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask", NoMatches, Ct);

        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["answer"]!.GetValue<string>().ShouldBe("I couldn't find any relevant information in the papers to answer your question.");
        (body["sources"]!.AsArray().Count, body["chunks_used"]!.GetValue<int>()).ShouldBe((0, 0));
        _api.Requests("/api/chat").ShouldBeEmpty();
        redis.AnswerKeys().ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_mode_says_bm25_when_the_query_could_not_be_embedded() // B24
    {
        _api.Fakes.Given(Request.Create().WithPath("/v1/embeddings").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(500));

        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask", Question, Ct);

        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["search_mode"]!.GetValue<string>().ShouldBe("bm25");
    }

    [Theory]
    [InlineData("/api/v1/ask", """{"query": "   "}""", "query")] // C13
    [InlineData("/api/v1/ask", """{"query": "q", "top_k": 11}""", "top_k")]
    [InlineData("/api/v1/stream", """{"query": ""}""", "query")]
    public async Task Invalid_questions_get_400(string path, string json, string field) // C1
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _api.Client().PostAsync(path, content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject().Select(e => e.Key).ShouldBe([field]);
    }

    [Fact]
    public async Task Ask_returns_503_when_opensearch_is_unreachable() // B6
    {
        var response = await _api.Client(new() { ["OpenSearch:Host"] = "http://127.0.0.1:1" })
            .PostAsJsonAsync("/api/v1/ask", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        _api.Requests("/api/chat").ShouldBeEmpty();
    }

    [Fact]
    public async Task Ask_returns_500_with_the_reason_when_ollama_fails()
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/chat").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"model 'qwen3.5:9b' not found"}"""));

        var response = await _api.Client().PostAsJsonAsync("/api/v1/ask", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>().ShouldContain("not found");
        redis.AnswerKeys().ShouldBeEmpty();
    }

    [Fact]
    public async Task Ask_still_answers_when_redis_is_down()
    {
        var response = await _api.Client(new() { ["ConnectionStrings:redis"] = "127.0.0.1:1" })
            .PostAsJsonAsync("/api/v1/ask", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["answer"]!.GetValue<string>().ShouldBe(Answer);
    }

    [Fact]
    public async Task Stream_sends_metadata_then_tokens_then_the_answer() // C2
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/stream", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        response.Headers.CacheControl!.NoCache.ShouldBeTrue();
        var events = await Events(response);
        Keys(events[0]).ShouldBe(["sources", "chunks_used", "search_mode"]);
        events[0]["chunks_used"]!.GetValue<int>().ShouldBe(2);
        events.Skip(1).SkipLast(1).Select(e => e["chunk"]!.GetValue<string>())
            .ShouldBe(["Agents maximise ", "long-term reward ", "[arXiv:2610.00002v1]."]);
        Keys(events[^1]).ShouldBe(["answer", "done"]);
        events[^1]["answer"]!.GetValue<string>().ShouldBe(Answer);
        redis.AnswerKeys().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Stream_replays_a_cached_answer_with_its_line_breaks() // B17
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/chat").UsingPost()).AtPriority(1).RespondWith(Response.Create().WithBody(
            """{"model":"qwen3.5:9b","created_at":"2026-10-02T00:00:00Z","message":{"role":"assistant","content":"Para one.\n\n- point\n- point"},"done":true}"""));
        var client = _api.Client();
        await client.PostAsJsonAsync("/api/v1/ask", Question, Ct);

        var events = await Events(await client.PostAsJsonAsync("/api/v1/stream", Question, Ct));

        string.Concat(events.Skip(1).SkipLast(1).Select(e => e["chunk"]!.GetValue<string>())).ShouldBe("Para one.\n\n- point\n- point");
        _api.Requests("/api/chat").Count().ShouldBe(1);
    }

    [Fact]
    public async Task Stream_without_matching_chunks_sends_one_event()
    {
        var events = await Events(await _api.Client().PostAsJsonAsync(
            "/api/v1/stream", NoMatches, Ct));

        events.ShouldHaveSingleItem().ToJsonString().ShouldBe("""{"answer":"No relevant information found.","sources":[],"done":true}""");
    }

    [Fact]
    public async Task Stream_ends_with_an_error_event_when_opensearch_is_unreachable() // B6
    {
        var response = await _api.Client(new() { ["OpenSearch:Host"] = "http://127.0.0.1:1" })
            .PostAsJsonAsync("/api/v1/stream", Question, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Keys((await Events(response)).ShouldHaveSingleItem()).ShouldBe(["error"]);
    }

    [Fact]
    public async Task OpenApi_describes_the_rag_endpoints()
    {
        var paths = (await _api.Client().GetFromJsonAsync<JsonObject>("/openapi/v1.json", Ct))!["paths"]!.AsObject();

        string[] expected = ["/api/v1/ask", "/api/v1/stream", "/api/v1/feedback", "/api/v1/models"];
        expected.ShouldBeSubsetOf(paths.Select(p => p.Key));
    }

    /// <summary>The <c>data:</c> payloads of a server-sent event stream.</summary>
    private static async Task<List<JsonObject>> Events(HttpResponseMessage response)
    {
        var events = new List<JsonObject>();
        foreach (var item in (await response.Content.ReadAsStringAsync(Ct)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            item.ShouldStartWith("data: ");
            events.Add(JsonNode.Parse(item["data: ".Length..])!.AsObject());
        }

        return events;
    }

    private static List<string> Keys(JsonObject json) => [.. json.Select(p => p.Key)];
}

using System.Net;
using System.Text.Json.Nodes;
using PaperPilot.Core.Contracts;
using PaperPilot.Web.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Web;

public sealed class PaperPilotApiClientTests : IDisposable
{
    private readonly WireMockServer _api = WireMockServer.Start();
    private readonly HttpClient _http;
    private readonly PaperPilotApiClient _client;

    public PaperPilotApiClientTests()
    {
        _http = new HttpClient { BaseAddress = new Uri($"{_api.Url}/") };
        _client = new PaperPilotApiClient(_http);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_posts_the_question_in_snake_case_and_yields_its_events()
    {
        _api.Given(Request.Create().WithPath("/api/v1/stream").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/event-stream")
                .WithBody(StreamEventReaderTests.Fixture("stream-error.sse")));

        var events = await _client.StreamAskAsync(
            new AskRequest { Query = "What is BERT?", TopK = 1, Categories = ["cs.CL"] }, Ct).ToArrayAsync(Ct);

        events.Select(e => e.GetType()).ShouldBe([typeof(StreamMetadata), typeof(StreamError)]);
        var request = _api.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Accept"].ShouldContain("text/event-stream");
        var body = JsonNode.Parse(request.Body!)!;
        (body["query"]!.GetValue<string>(), body["top_k"]!.GetValue<int>(), body["use_hybrid"]!.GetValue<bool>())
            .ShouldBe(("What is BERT?", 1, true));
        body["categories"]![0]!.GetValue<string>().ShouldBe("cs.CL");
    }

    [Fact]
    public async Task An_agentic_answer_is_read_from_snake_case()
    {
        Respond("/api/v1/ask-agentic", 200, new JsonObject
        {
            ["query"] = "q",
            ["answer"] = "A.",
            ["sources"] = new JsonArray("https://arxiv.org/pdf/2610.00820.pdf"),
            ["chunks_used"] = 3,
            ["search_mode"] = "hybrid",
            ["reasoning_steps"] = new JsonArray("Validated query scope (score: 90/100)"),
            ["retrieval_attempts"] = 1,
            ["trace_id"] = "0af7651916cd43dd8448eb211c80319c",
        });

        var response = await _client.AskAgenticAsync(new AskRequest { Query = "q" }, Ct);

        (response.ChunksUsed, response.RetrievalAttempts, response.TraceId).ShouldBe((3, 1, "0af7651916cd43dd8448eb211c80319c"));
        response.ReasoningSteps.ShouldBe(["Validated query scope (score: 90/100)"]);
    }

    [Fact]
    public async Task A_problem_response_becomes_an_api_exception_with_its_detail()
    {
        Respond("/api/v1/ask-agentic", 500, new JsonObject { ["title"] = "Server error", ["detail"] = "Error processing question: boom" });

        var ex = await Should.ThrowAsync<ApiException>(() => _client.AskAgenticAsync(new AskRequest { Query = "q" }, Ct));

        ex.Message.ShouldBe("Error processing question: boom");
        ex.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task A_validation_problem_gives_its_first_error()
    {
        Respond("/api/v1/stream", 400, new JsonObject
        {
            ["title"] = "One or more validation errors occurred.",
            ["errors"] = new JsonObject { ["query"] = new JsonArray("The field query must be a string with a maximum length of 1000.") },
        });

        var ex = await Should.ThrowAsync<ApiException>(
            async () => await _client.StreamAskAsync(new AskRequest { Query = "q" }, Ct).ToArrayAsync(Ct));

        ex.Message.ShouldBe("The field query must be a string with a maximum length of 1000.");
    }

    [Fact]
    public async Task A_response_without_a_problem_body_gives_its_status()
    {
        _api.Given(Request.Create().WithPath("/api/v1/models"))
            .RespondWith(Response.Create().WithStatusCode(502).WithBody("<html>Bad gateway</html>"));

        var ex = await Should.ThrowAsync<ApiException>(() => _client.GetModelsAsync(Ct));

        ex.Message.ShouldBe("502 Bad Gateway");
    }

    [Fact]
    public async Task An_unreachable_api_is_reported_as_such()
    {
        _api.Stop();

        var ex = await Should.ThrowAsync<ApiException>(() => _client.GetModelsAsync(Ct));

        ex.Message.ShouldStartWith("Cannot reach the PaperPilot API: ");
        ex.StatusCode.ShouldBeNull();
    }

    [Theory]
    [InlineData(200, FeedbackResult.Recorded)]
    [InlineData(503, FeedbackResult.Unavailable)]
    public async Task Feedback_is_recorded_or_reported_unavailable(int status, FeedbackResult expected)
    {
        Respond("/api/v1/feedback", status, status == 200
            ? new JsonObject { ["success"] = true, ["message"] = "Feedback recorded successfully" }
            : new JsonObject { ["detail"] = "Langfuse tracing is disabled. Cannot submit feedback." });

        var result = await _client.SubmitFeedbackAsync(
            new FeedbackRequest { TraceId = "0af7651916cd43dd8448eb211c80319c", Score = -1, Comment = "Off topic" }, Ct);

        result.ShouldBe(expected);
        var body = JsonNode.Parse(_api.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!)!;
        (body["trace_id"]!.GetValue<string>(), body["score"]!.GetValue<double>(), body["comment"]!.GetValue<string>())
            .ShouldBe(("0af7651916cd43dd8448eb211c80319c", -1, "Off topic"));
    }

    [Fact]
    public async Task Models_are_read()
    {
        Respond("/api/v1/models", 200, new JsonObject { ["default_model"] = "qwen3.5:9b", ["models"] = new JsonArray("qwen3.5:9b") });

        var models = await _client.GetModelsAsync(Ct);

        models.DefaultModel.ShouldBe("qwen3.5:9b");
        models.Models.ShouldBe(["qwen3.5:9b"]);
    }

    public void Dispose()
    {
        _http.Dispose();
        _api.Dispose();
    }

    private void Respond(string path, int status, JsonObject body) =>
        _api.Given(Request.Create().WithPath(path))
            .RespondWith(Response.Create().WithStatusCode(status)
                .WithHeader("Content-Type", status >= 400 ? "application/problem+json" : "application/json")
                .WithBody(body.ToJsonString()));
}

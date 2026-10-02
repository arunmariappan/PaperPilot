using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PaperPilot.IntegrationTests.Caching;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace PaperPilot.IntegrationTests.Api;

/// <summary><c>/feedback</c> with WireMock as Langfuse, and <c>/models</c> with WireMock as Ollama.</summary>
[Collection(ContainersCollectionDefinition.Name)]
public sealed class FeedbackAndModelsApiTests(PostgresFixture postgres, OpenSearchFixture search, RedisFixture redis) : IAsyncLifetime
{
    private const string TraceId = "4bf92f3577b34da6a3ce929d0e0e4736";

    private readonly ApiHost _api = new(postgres, search, redis);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Feedback => new { trace_id = TraceId, score = 1, comment = "Spot on." };

    public ValueTask InitializeAsync()
    {
        _api.StubOllamaAndJina(new float[1024]);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _api.DisposeAsync();

    [Fact]
    public async Task Feedback_returns_503_when_langfuse_is_disabled() // B16
    {
        var response = await _api.Client().PostAsJsonAsync("/api/v1/feedback", Feedback, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>()
            .ShouldBe("Langfuse tracing is disabled. Cannot submit feedback.");
    }

    [Fact]
    public async Task Feedback_is_recorded_as_a_langfuse_score()
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/public/scores").UsingPost())
            .RespondWith(Response.Create().WithBody("""{"id":"x"}"""));

        var response = await LangfuseClient().PostAsJsonAsync("/api/v1/feedback", Feedback, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!.ToJsonString()
            .ShouldBe("""{"success":true,"message":"Feedback recorded successfully"}""");
        var score = JsonNode.Parse(_api.Requests("/api/public/scores").ShouldHaveSingleItem().Body!)!;
        (score["traceId"]!.GetValue<string>(), score["name"]!.GetValue<string>(), score["value"]!.GetValue<double>())
            .ShouldBe((TraceId, "user-feedback", 1.0));
        score["comment"]!.GetValue<string>().ShouldBe("Spot on.");
    }

    [Fact]
    public async Task Feedback_returns_500_when_langfuse_rejects_the_score()
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/public/scores").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"message":"Invalid request data"}"""));

        var response = await LangfuseClient().PostAsJsonAsync("/api/v1/feedback", Feedback, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>()
            .ShouldStartWith("Failed to submit feedback to Langfuse: Langfuse returned 400");
    }

    [Theory]
    [InlineData("""{"trace_id": "t", "score": 2}""", "score")]
    [InlineData("""{"trace_id": "t"}""", "score")]
    [InlineData("""{"score": 1}""", "trace_id")]
    public async Task Invalid_feedback_gets_400(string json, string field)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var response = await LangfuseClient().PostAsync("/api/v1/feedback", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject().Select(e => e.Key).ShouldBe([field]);
    }

    [Fact]
    public async Task Models_lists_the_installed_ollama_models() // N1
    {
        _api.Fakes.Given(Request.Create().WithPath("/api/tags").UsingGet()).RespondWith(Response.Create().WithBody(
            """{"models":[{"name":"qwen3.5:9b","model":"qwen3.5:9b"},{"name":"llama3.2:1b","model":"llama3.2:1b"}]}"""));

        var body = await _api.Client().GetFromJsonAsync<JsonObject>("/api/v1/models", Ct);

        body!.ToJsonString().ShouldBe("""{"default_model":"qwen3.5:9b","models":["llama3.2:1b","qwen3.5:9b"]}""");
    }

    [Fact]
    public async Task Models_returns_503_when_ollama_is_unreachable()
    {
        var response = await _api.Client(new() { ["ConnectionStrings:ollama"] = "Endpoint=http://127.0.0.1:1" })
            .GetAsync("/api/v1/models", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>().ShouldStartWith("Cannot list Ollama models:");
    }

    private HttpClient LangfuseClient() => _api.Client(new()
    {
        ["Langfuse:Enabled"] = "true",
        ["Langfuse:BaseUrl"] = _api.Fakes.Url,
        ["Langfuse:PublicKey"] = "pk-lf-test",
        ["Langfuse:SecretKey"] = "sk-lf-test",
    });
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PaperPilot.IntegrationTests.Caching;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace PaperPilot.IntegrationTests.Api;

/// <summary>
/// The real API in-process, against the Postgres, OpenSearch and Redis containers, with one WireMock server standing
/// in for Jina (<c>/v1/embeddings</c>), Ollama (<c>/api/*</c>) and Langfuse (<c>/api/public/*</c>).
/// </summary>
internal sealed class ApiHost(PostgresFixture postgres, OpenSearchFixture search, RedisFixture redis) : IAsyncDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public WireMockServer Fakes { get; } = WireMockServer.Start();

    /// <summary>A client for a fresh API instance; <paramref name="settings"/> override the defaults.</summary>
    public HttpClient Client(Dictionary<string, string?>? settings = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:papers", postgres.ConnectionString);
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("ConnectionStrings:ollama", $"Endpoint={Fakes.Url}");
            builder.UseSetting("OpenSearch:Host", search.Url);
            builder.UseSetting("Jina:BaseUrl", $"{Fakes.Url}/v1/");
            builder.UseSetting("Jina:ApiKey", "test-key");
            foreach (var (key, value) in settings ?? [])
            {
                builder.UseSetting(key, value);
            }
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    /// <summary>Ollama's version endpoint (health check), and Jina embedding every query to <paramref name="vector"/>.</summary>
    public void StubOllamaAndJina(IReadOnlyList<float> vector)
    {
        Fakes.Given(Request.Create().WithPath("/api/version").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"version":"0.35.0"}"""));
        Fakes.Given(Request.Create().WithPath("/v1/embeddings").UsingPost()).AtPriority(10)
            .RespondWith(Response.Create().WithCallback(_ => Json(new JsonObject
            {
                ["model"] = "jina-embeddings-v3",
                ["data"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["embedding"] = new JsonArray([.. vector.Select(v => (JsonNode)v)]),
                }),
            })));
    }

    /// <summary>Ollama <c>/api/chat</c> answering with <paramref name="tokens"/>: one JSON reply, or NDJSON when streaming.</summary>
    public void StubChat(params string[] tokens) =>
        Fakes.Given(Request.Create().WithPath("/api/chat").UsingPost()).AtPriority(10)
            .RespondWith(Response.Create().WithCallback(request =>
                JsonNode.Parse(request.Body!)!["stream"]!.GetValue<bool>() ? ChatStream(tokens) : Json(ChatMessage(string.Concat(tokens), done: true))));

    /// <summary>Ollama <c>/api/chat</c> answering each request with <paramref name="reply"/>(the last message's text).</summary>
    public void StubChat(Func<string, string> reply) =>
        Fakes.Given(Request.Create().WithPath("/api/chat").UsingPost()).AtPriority(10)
            .RespondWith(Response.Create().WithCallback(request =>
            {
                var body = JsonNode.Parse(request.Body!)!;
                var text = reply(body["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>());
                return body["stream"]!.GetValue<bool>() ? ChatStream([text]) : Json(ChatMessage(text, done: true));
            }));

    public IEnumerable<IRequestMessage> Requests(string path) =>
        Fakes.LogEntries.Select(e => e.RequestMessage!).Where(r => r.Path == path);

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        Fakes.Dispose();
    }

    private static JsonObject ChatMessage(string content, bool done)
    {
        var message = new JsonObject
        {
            ["model"] = "qwen3.5:9b",
            ["created_at"] = "2026-10-02T00:00:00Z",
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["done"] = done,
        };
        if (done)
        {
            message["done_reason"] = "stop";
            message["prompt_eval_count"] = 120;
            message["eval_count"] = 30;
        }

        return message;
    }

    private static ResponseMessage ChatStream(string[] tokens)
    {
        var lines = tokens.Select(t => ChatMessage(t, done: false).ToJsonString()).Append(ChatMessage("", done: true).ToJsonString());
        return Body(string.Join('\n', lines) + "\n", "application/x-ndjson");
    }

    private static ResponseMessage Json(JsonNode body) => Body(body.ToJsonString(), "application/json");

    private static ResponseMessage Body(string body, string contentType)
    {
        var response = new ResponseMessage
        {
            StatusCode = 200,
            BodyData = new BodyData { BodyAsString = body, DetectedBodyType = BodyType.String, Encoding = Encoding.UTF8 },
        };
        response.AddHeader("Content-Type", contentType);
        return response;
    }
}

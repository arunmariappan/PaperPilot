using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace PaperPilot.IntegrationTests.Api;

/// <summary>The real API in-process, against the Postgres and OpenSearch containers, with WireMock as Jina and Ollama.</summary>
[Collection(ContainersCollectionDefinition.Name)]
public sealed class SearchApiTests(PostgresFixture postgres, OpenSearchFixture search) : IAsyncLifetime
{
    private readonly WireMockServer _fakes = WireMockServer.Start();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await search.Client.EnsureIndexAsync(force: true, Ct);
        await search.Client.EnsureRrfPipelineAsync(cancellationToken: Ct);
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);

        _fakes.Given(Request.Create().WithPath("/api/version").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody("""{"version":"0.35.0"}"""));
        _fakes.Given(Request.Create().WithPath("/v1/embeddings").UsingPost()).AtPriority(10)
            .RespondWith(Response.Create().WithCallback(RewardEmbedding));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }

        _fakes.Dispose();
    }

    [Fact]
    public async Task Bm25_search_returns_the_python_response_shape()
    {
        var response = await Client().PostAsJsonAsync(
            "/api/v1/hybrid-search/", new { query = "self-attention transformers", use_hybrid = false }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body.Select(p => p.Key).ShouldBe(["query", "total", "hits", "size", "from", "search_mode", "error"], ignoreOrder: true);
        body["search_mode"]!.GetValue<string>().ShouldBe("bm25");
        body["size"]!.GetValue<int>().ShouldBe(10);
        var top = body["hits"]![0]!;
        top["chunk_id"]!.GetValue<string>().ShouldBe("2610.00001v1:0");
        top["section_name"]!.GetValue<string>().ShouldBe("Introduction"); // B7
        top["pdf_url"]!.GetValue<string>().ShouldBe("https://arxiv.org/pdf/2610.00001.pdf"); // B7
        _fakes.LogEntries.ShouldNotContain(e => e.RequestMessage!.Path == "/v1/embeddings");
    }

    [Fact]
    public async Task Hybrid_search_embeds_the_query()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/hybrid-search/", new { query = "reward", size = 3 }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["search_mode"]!.GetValue<string>().ShouldBe("hybrid");
        body["hits"]![0]!["arxiv_id"]!.GetValue<string>().ShouldBe("2610.00002v1");
        _fakes.LogEntries.Count(e => e.RequestMessage!.Path == "/v1/embeddings").ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_embedding_falls_back_to_bm25()
    {
        _fakes.Given(Request.Create().WithPath("/v1/embeddings").UsingPost()).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("embedding service down"));

        var response = await Client().PostAsJsonAsync("/api/v1/hybrid-search/", new { query = "reward" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["search_mode"]!.GetValue<string>().ShouldBe("bm25");
    }

    [Theory]
    [InlineData("/api/v1/hybrid-search/")]
    [InlineData("/api/v1/hybrid-search")]
    public async Task The_trailing_slash_is_optional(string path)
    {
        var response = await Client().PostAsJsonAsync(path, new { query = "reward", use_hybrid = false }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("""{"query": ""}""", "query")]
    [InlineData("""{"query": "q", "size": 101}""", "size")]
    [InlineData("""{"query": "q", "from": -1}""", "from")]
    [InlineData("""{"query": "q", "min_score": -1}""", "min_score")]
    [InlineData("""{"size": 5}""", "query")]
    public async Task Invalid_requests_get_400_problem_details_keyed_by_field(string json, string field) // C1
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await Client().PostAsync("/api/v1/hybrid-search/", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject().Select(e => e.Key).ShouldBe([field]);
    }

    [Fact]
    public async Task Search_returns_503_when_opensearch_is_unreachable() // B6
    {
        var response = await Client(openSearchHost: "http://127.0.0.1:1")
            .PostAsJsonAsync("/api/v1/hybrid-search/", new { query = "reward" }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["detail"]!.GetValue<string>()
            .ShouldBe("Search service is currently unavailable");
    }

    [Fact]
    public async Task Health_reports_every_dependency_in_the_python_shape()
    {
        var response = await Client().GetAsync("/api/v1/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["status"]!.GetValue<string>().ShouldBe("ok");
        body["environment"]!.GetValue<string>().ShouldBe("development");
        (body["version"]!.GetValue<string>(), body["service_name"]!.GetValue<string>()).ShouldBe(("0.1.0", "rag-api"));
        var services = body["services"]!;
        services["database"]!["message"]!.GetValue<string>().ShouldBe("Connected successfully");
        services["opensearch"]!["message"]!.GetValue<string>().ShouldBe("Index 'arxiv-papers-chunks' with 3 documents");
        services["ollama"]!["message"]!.GetValue<string>().ShouldBe("Ollama service is running");
        services.AsObject().ShouldAllBe(s => s.Value!["status"]!.GetValue<string>() == "healthy");
    }

    [Fact]
    public async Task Health_is_still_200_but_degraded_when_opensearch_is_down()
    {
        var response = await Client(openSearchHost: "http://127.0.0.1:1").GetAsync("/api/v1/health", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        body["status"]!.GetValue<string>().ShouldBe("degraded");
        body["services"]!["opensearch"]!["status"]!.GetValue<string>().ShouldBe("unhealthy");
        body["services"]!["opensearch"]!["message"]!.GetValue<string>().ShouldBe("Not responding");
    }

    [Fact]
    public async Task OpenApi_describes_the_endpoints_in_snake_case() // C11
    {
        var document = (await Client().GetFromJsonAsync<JsonObject>("/openapi/v1.json", Ct))!;

        document["paths"]!.AsObject().ShouldContain(p => p.Key.TrimEnd('/') == "/api/v1/hybrid-search");
        document["paths"]!.AsObject().ShouldContain(p => p.Key == "/api/v1/health");
        document.ToJsonString().ShouldContain("\"use_hybrid\"");
        (await Client().GetAsync("/docs", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private HttpClient Client(string? openSearchHost = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:papers", postgres.ConnectionString);
            builder.UseSetting("ConnectionStrings:ollama", $"Endpoint={_fakes.Url}");
            builder.UseSetting("OpenSearch:Host", openSearchHost ?? search.Url);
            builder.UseSetting("Jina:BaseUrl", $"{_fakes.Url}/v1/");
            builder.UseSetting("Jina:ApiKey", "test-key");
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }

    /// <summary>Every query embeds to the "reward" chunk's vector.</summary>
    private static ResponseMessage RewardEmbedding(IRequestMessage request)
    {
        var body = new JsonObject
        {
            ["model"] = "jina-embeddings-v3",
            ["data"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["embedding"] = new JsonArray([.. SampleChunks.Reward.Embedding.Select(v => (JsonNode)v)]),
            }),
        };
        var response = new ResponseMessage
        {
            StatusCode = 200,
            BodyData = new BodyData { BodyAsString = body.ToJsonString(), DetectedBodyType = BodyType.String, Encoding = Encoding.UTF8 },
        };
        response.AddHeader("Content-Type", "application/json");
        return response;
    }
}

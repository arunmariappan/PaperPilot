using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Search;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace PaperPilot.UnitTests.Embeddings;

public sealed class JinaEmbeddingServiceTests : IDisposable
{
    private const int Dimensions = 4;

    private readonly WireMockServer _jina = WireMockServer.Start();
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IRequestBuilder EmbeddingsRequest => Request.Create().WithPath("/v1/embeddings").UsingPost();

    [Fact]
    public async Task Query_request_matches_the_python_client()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithCallback(Embeddings));

        var vector = await Service().EmbedQueryAsync("attention mechanisms", Ct);

        vector.Length.ShouldBe(Dimensions);
        var request = _jina.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe(["Bearer test-key"]);
        var body = JsonNode.Parse(request.Body!)!;
        body["model"]!.GetValue<string>().ShouldBe("jina-embeddings-v3");
        body["task"]!.GetValue<string>().ShouldBe("retrieval.query");
        body["dimensions"]!.GetValue<int>().ShouldBe(Dimensions);
        body["late_chunking"]!.GetValue<bool>().ShouldBeFalse();
        body["embedding_type"]!.GetValue<string>().ShouldBe("float");
        body["input"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe(["attention mechanisms"]);
    }

    [Fact]
    public async Task A_rate_limited_query_waits_for_Retry_After_and_then_succeeds()
    {
        _jina.Given(EmbeddingsRequest).InScenario("rate-limit").WillSetStateTo("passed")
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "1"));
        _jina.Given(EmbeddingsRequest).InScenario("rate-limit").WhenStateIs("passed")
            .RespondWith(Response.Create().WithCallback(Embeddings));
        var stopwatch = Stopwatch.StartNew();

        var vector = await Service().EmbedQueryAsync("q", Ct);

        vector.Length.ShouldBe(Dimensions);
        _jina.LogEntries.Count.ShouldBe(2);
        stopwatch.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(0.9));
    }

    [Fact]
    public async Task A_query_gives_up_after_two_retries()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));

        var error = await Should.ThrowAsync<EmbeddingUnavailableException>(() => Service().EmbedQueryAsync("q", Ct));

        error.Message.ShouldContain("429");
        _jina.LogEntries.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Passages_retry_up_to_six_times()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "0"));

        await Should.ThrowAsync<EmbeddingUnavailableException>(() => Service().EmbedPassagesAsync(["p0"], cancellationToken: Ct));

        _jina.LogEntries.Count.ShouldBe(7);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));

        await Should.ThrowAsync<EmbeddingUnavailableException>(() => Service().EmbedQueryAsync("q", Ct));

        _jina.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_missing_key_fails_without_calling_Jina()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithCallback(Embeddings));

        await Should.ThrowAsync<EmbeddingUnavailableException>(() => Service(apiKey: "").EmbedQueryAsync("q", Ct));

        _jina.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Passages_are_embedded_in_batches_and_returned_in_input_order()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithCallback(Embeddings));
        string[] passages = ["p0", "p1", "p2", "p3", "p4"];

        var vectors = await Service().EmbedPassagesAsync(passages, batchSize: 2, Ct);

        _jina.LogEntries.Count.ShouldBe(3);
        vectors.Select(v => v[0]).ShouldBe([0f, 1f, 2f, 3f, 4f]);
        JsonNode.Parse(_jina.LogEntries[0].RequestMessage!.Body!)!["task"]!.GetValue<string>().ShouldBe("retrieval.passage");
    }

    [Fact]
    public async Task A_response_with_the_wrong_number_of_vectors_is_an_error()
    {
        _jina.Given(EmbeddingsRequest).RespondWith(Response.Create().WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody("""{"model": "jina-embeddings-v3", "data": []}"""));

        var error = await Should.ThrowAsync<EmbeddingUnavailableException>(() => Service().EmbedQueryAsync("q", Ct));

        error.Message.ShouldContain("0 vectors for 1 inputs");
    }

    public void Dispose()
    {
        _hosts.ForEach(h => h.Dispose());
        _jina.Dispose();
    }

    private IEmbeddingService Service(string apiKey = "test-key")
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{JinaOptions.SectionName}:BaseUrl"] = $"{_jina.Url}/v1/",
            [$"{JinaOptions.SectionName}:ApiKey"] = apiKey,
            [$"{OpenSearchOptions.SectionName}:VectorDimension"] = Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.AddServiceDefaults();
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotSearch();
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<IEmbeddingService>();
    }

    /// <summary>One vector per input; a passage "pN" gets [N, 0, 0, 0]. Items come back in reverse to test ordering.</summary>
    private static ResponseMessage Embeddings(IRequestMessage request)
    {
        var inputs = JsonNode.Parse(request.Body!)!["input"]!.AsArray();
        var data = new JsonArray();
        for (var i = inputs.Count - 1; i >= 0; i--)
        {
            var text = inputs[i]!.GetValue<string>();
            var first = text.StartsWith('p') && int.TryParse(text[1..], out var n) ? n : 0.5f;
            data.Add(new JsonObject
            {
                ["object"] = "embedding",
                ["index"] = i,
                ["embedding"] = new JsonArray(first, 0f, 0f, 0f),
            });
        }

        var body = new JsonObject
        {
            ["model"] = "jina-embeddings-v3",
            ["object"] = "list",
            ["usage"] = new JsonObject { ["total_tokens"] = 1, ["prompt_tokens"] = 1 },
            ["data"] = data,
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

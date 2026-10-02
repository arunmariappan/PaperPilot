using System.Text.Json;
using System.Text.Json.Nodes;
using PaperPilot.Core.Contracts;

namespace PaperPilot.UnitTests.Contracts;

/// <summary>
/// The Python schemas' <c>json_schema_extra</c> examples must read into the C# contracts and come back out
/// with the same snake_case keys and values.
/// </summary>
public sealed class ContractJsonTests
{
    [Fact]
    public void AskRequest_round_trips_the_Python_example() =>
        RoundTrip<AskRequest>("""
            {"query": "What are transformers in machine learning?", "top_k": 3, "use_hybrid": true,
             "model": "llama3.2:1b", "categories": ["cs.AI", "cs.LG"]}
            """);

    [Fact]
    public void AskRequest_defaults_match_Python()
    {
        var request = JsonSerializer.Deserialize<AskRequest>("""{"query": "q"}""", ApiJson.Options)!;

        (request.TopK, request.UseHybrid, request.Model, request.Categories).ShouldBe((3, true, (string?)null, (IReadOnlyList<string>?)null));
    }

    [Fact]
    public void AskResponse_round_trips_the_Python_example() =>
        RoundTrip<AskResponse>("""
            {"query": "What are transformers in machine learning?",
             "answer": "Transformers are a neural network architecture...",
             "sources": ["https://arxiv.org/pdf/1706.03762.pdf", "https://arxiv.org/pdf/1810.04805.pdf"],
             "chunks_used": 3, "search_mode": "hybrid"}
            """);

    [Fact]
    public void AgenticAskResponse_round_trips_the_Python_example() =>
        RoundTrip<AgenticAskResponse>("""
            {"query": "What are transformers in machine learning?",
             "answer": "Transformers are neural network architectures...",
             "sources": ["https://arxiv.org/pdf/1706.03762.pdf"], "chunks_used": 3, "search_mode": "hybrid",
             "reasoning_steps": ["Decided to retrieve relevant papers", "Retrieved documents from database",
                                 "Generated answer from relevant documents"],
             "retrieval_attempts": 1, "trace_id": "abc123-def456-ghi789"}
            """);

    [Fact]
    public void FeedbackRequest_round_trips_the_Python_example() =>
        RoundTrip<FeedbackRequest>("""
            {"trace_id": "abc123-def456-ghi789", "score": 1.0,
             "comment": "This answer was very helpful and accurate!"}
            """);

    [Fact]
    public void FeedbackResponse_round_trips_the_Python_example() =>
        RoundTrip<FeedbackResponse>("""{"success": true, "message": "Feedback recorded successfully"}""");

    [Fact]
    public void HybridSearchRequest_round_trips_the_Python_example_and_fills_defaults()
    {
        var output = RoundTrip<HybridSearchRequest>(
            """
            {"query": "machine learning neural networks", "size": 10, "categories": ["cs.AI", "cs.LG"],
             "latest_papers": false, "use_hybrid": true}
            """,
            exactKeys: false);

        output.Select(p => p.Key).ShouldBe(
            ["query", "size", "from", "categories", "latest_papers", "use_hybrid", "min_score"], ignoreOrder: true);
        output["from"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void HealthResponse_round_trips_the_Python_example() =>
        RoundTrip<HealthResponse>("""
            {"status": "ok", "version": "0.1.0", "environment": "development", "service_name": "rag-api",
             "services": {"database": {"status": "healthy", "message": "Connected successfully"},
                          "pdf_parser": {"status": "healthy", "message": "Docling parser ready"}}}
            """);

    [Fact]
    public void SearchResponse_uses_the_Python_field_names()
    {
        var response = new SearchResponse
        {
            Query = "q",
            Total = 1,
            Size = 10,
            From = 0,
            SearchMode = "bm25",
            Hits = [new SearchHit { ArxivId = "2510.01234v1", Title = "T", Score = 1.5 }],
        };

        var json = JsonSerializer.SerializeToNode(response, ApiJson.Options)!.AsObject();

        json.Select(p => p.Key).ShouldBe(
            ["query", "total", "hits", "size", "from", "search_mode", "error"], ignoreOrder: true);
        json["hits"]![0]!.AsObject().Select(p => p.Key).ShouldBe(
            ["arxiv_id", "title", "authors", "abstract", "published_date", "pdf_url", "score", "highlights",
             "chunk_text", "chunk_id", "section_name"],
            ignoreOrder: true);
    }

    /// <summary>Deserializes <paramref name="json"/>, serializes it back, and checks that nothing changed.</summary>
    private static JsonObject RoundTrip<T>(string json, bool exactKeys = true)
    {
        var expected = JsonNode.Parse(json)!.AsObject();
        var value = JsonSerializer.Deserialize<T>(json, ApiJson.Options);
        var actual = JsonSerializer.SerializeToNode(value, ApiJson.Options)!.AsObject();

        if (exactKeys)
        {
            actual.Select(p => p.Key).ShouldBe(expected.Select(p => p.Key), ignoreOrder: true);
        }

        foreach (var (key, node) in expected)
        {
            actual.ContainsKey(key).ShouldBeTrue($"missing key '{key}'");
            AssertEquivalent(node, actual[key], key);
        }

        return actual;
    }

    private static void AssertEquivalent(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null:
                actual.ShouldBeNull(path);
                break;
            case JsonObject obj:
                var actualObject = actual.ShouldBeOfType<JsonObject>(path);
                actualObject.Select(p => p.Key).ShouldBe(obj.Select(p => p.Key), ignoreOrder: true, path);
                foreach (var (key, node) in obj)
                {
                    AssertEquivalent(node, actualObject[key], $"{path}.{key}");
                }

                break;
            case JsonArray array:
                var actualArray = actual.ShouldBeOfType<JsonArray>(path);
                actualArray.Count.ShouldBe(array.Count, path);
                for (var i = 0; i < array.Count; i++)
                {
                    AssertEquivalent(array[i], actualArray[i], $"{path}[{i}]");
                }

                break;
            default:
                var expectedValue = expected.AsValue();
                if (expectedValue.GetValueKind() == JsonValueKind.Number)
                {
                    actual!.GetValue<decimal>().ShouldBe(expectedValue.GetValue<decimal>(), path);
                }
                else
                {
                    actual!.ToJsonString().ShouldBe(expected.ToJsonString(), path);
                }

                break;
        }
    }
}

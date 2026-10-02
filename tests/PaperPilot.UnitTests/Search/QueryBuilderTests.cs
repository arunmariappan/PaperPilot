using System.Text.Json.Nodes;
using PaperPilot.Core.Search;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Search;

/// <summary>Golden tests: every request body must equal what the Python query builder produced.</summary>
public sealed class QueryBuilderTests
{
    private static readonly JsonArray Bm25Cases = ParityFixtures.Load("search/bm25-queries.json").AsArray();
    private static readonly JsonArray HybridCases = ParityFixtures.Load("search/hybrid-queries.json").AsArray();

    public static TheoryData<string> Bm25CaseNames => [.. Bm25Cases.Select(c => c!["name"]!.GetValue<string>())];

    public static TheoryData<string> HybridCaseNames => [.. HybridCases.Select(c => c!["name"]!.GetValue<string>())];

    [Fact]
    public void All_python_cases_are_covered() => (Bm25Cases.Count, HybridCases.Count).ShouldBe((36, 3));

    [Theory]
    [MemberData(nameof(Bm25CaseNames))]
    public void Bm25_body_matches_python(string name)
    {
        var testCase = Bm25Cases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var input = testCase["input"]!;

        var body = QueryBuilder.BuildBm25(new SearchQuery(
            input["query"]!.GetValue<string>(),
            input["size"]!.GetValue<int>(),
            input["from"]!.GetValue<int>(),
            Categories(input["categories"]),
            input["latest_papers"]!.GetValue<bool>()));

        JsonAssert.Equivalent(testCase["expected"], body);
    }

    [Theory]
    [MemberData(nameof(HybridCaseNames))]
    public void Hybrid_body_matches_python(string name)
    {
        var testCase = HybridCases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var input = testCase["input"]!;
        var embedding = input["embedding"]!.AsArray().Select(v => v!.GetValue<float>()).ToArray();

        var body = QueryBuilder.BuildHybrid(
            new SearchQuery(input["query"]!.GetValue<string>(), input["size"]!.GetValue<int>(), Categories: Categories(input["categories"])),
            embedding,
            multiplier: 2);

        var expected = testCase["expected"]!["body"]!.DeepClone();
        if (Categories(input["categories"]) is { Length: > 0 })
        {
            // B30: Python left the k-NN arm unfiltered; PaperPilot wraps it in the BM25 arm's category filter.
            var queries = expected["query"]!["hybrid"]!["queries"]!.AsArray();
            queries[1] = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["must"] = new JsonArray(queries[1]!.DeepClone()),
                    ["filter"] = queries[0]!["bool"]!["filter"]!.DeepClone(),
                },
            };
        }

        JsonAssert.Equivalent(expected, body);
        testCase["expected"]!["params"]!["search_pipeline"]!.GetValue<string>().ShouldBe("hybrid-rrf-pipeline");
    }

    [Fact]
    public void Hybrid_ignores_from_and_latest_and_uses_the_configured_multiplier()
    {
        var body = QueryBuilder.BuildHybrid(new SearchQuery("q", Size: 4, From: 40, LatestPapers: true), [1f, 0f], multiplier: 3);

        var queries = body["query"]!["hybrid"]!["queries"]!.AsArray();
        queries[1]!["knn"]!["embedding"]!["k"]!.GetValue<int>().ShouldBe(12);
        body.ContainsKey("from").ShouldBeFalse();
        body.ContainsKey("sort").ShouldBeFalse();
        body["size"]!.GetValue<int>().ShouldBe(4);
    }

    private static string[]? Categories(JsonNode? node) =>
        node?.AsArray().Select(c => c!.GetValue<string>()).ToArray();
}

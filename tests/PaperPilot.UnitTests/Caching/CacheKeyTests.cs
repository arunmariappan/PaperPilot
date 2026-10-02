using System.Text.Json.Nodes;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Caching;

public sealed class CacheKeyTests
{
    private static readonly JsonArray Cases = ParityFixtures.Load("rag/cache-keys.json").AsArray();

    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c!["name"]!.GetValue<string>())];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void The_hash_matches_python(string name)
    {
        var testCase = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var input = testCase["input"]!;
        var request = new AskRequest
        {
            Query = input["query"]!.GetValue<string>(),
            TopK = input["top_k"]!.GetValue<int>(),
            UseHybrid = input["use_hybrid"]!.GetValue<bool>(),
            Categories = input["categories"]?.AsArray().Select(c => c!.GetValue<string>()).ToList(),
        };

        var key = CacheKey.Compute(request, input["model"]!.GetValue<string>());

        key.ShouldBe("paperpilot:ask:" + testCase["expected_hash"]!.GetValue<string>()); // C6: only the prefix differs
    }

    [Fact]
    public void The_key_is_stable_and_ignores_category_order()
    {
        var request = new AskRequest { Query = "q", Categories = ["cs.LG", "cs.AI"] };

        CacheKey.Compute(request, "m").ShouldBe(CacheKey.Compute(request, "m"));
        CacheKey.Compute(request with { Categories = ["cs.AI", "cs.LG"] }, "m").ShouldBe(CacheKey.Compute(request, "m"));
        CacheKey.Compute(request with { Categories = null }, "m")
            .ShouldBe(CacheKey.Compute(request with { Categories = [] }, "m"));
    }

    [Fact]
    public void Every_field_that_changes_the_answer_changes_the_key()
    {
        var request = new AskRequest { Query = "q" };
        var key = CacheKey.Compute(request, "m");

        CacheKey.Compute(request, "other-model").ShouldNotBe(key);
        CacheKey.Compute(request with { Query = "q2" }, "m").ShouldNotBe(key);
        CacheKey.Compute(request with { TopK = 4 }, "m").ShouldNotBe(key);
        CacheKey.Compute(request with { UseHybrid = false }, "m").ShouldNotBe(key);
        CacheKey.Compute(request with { Categories = ["cs.AI"] }, "m").ShouldNotBe(key);
    }
}

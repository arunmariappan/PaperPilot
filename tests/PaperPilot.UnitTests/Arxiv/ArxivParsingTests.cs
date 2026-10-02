using System.Globalization;
using System.Text.Json.Nodes;
using PaperPilot.Core.Exceptions;
using PaperPilot.Infrastructure.Arxiv;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Arxiv;

public sealed class ArxivParsingTests
{
    private static readonly JsonArray UrlCases = ParityFixtures.Load("arxiv/urls.json").AsArray();

    public static TheoryData<string> UrlCaseNames => [.. UrlCases.Select(c => c!["name"]!.GetValue<string>())];

    [Theory]
    [MemberData(nameof(UrlCaseNames))]
    public void Urls_match_python(string name)
    {
        var testCase = UrlCases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var input = testCase["input"]!;

        var url = name == "by-id"
            ? ArxivQueryBuilder.BuildIdUrl("https://export.arxiv.org/api/query", input["id"]!.GetValue<string>())
            : ArxivQueryBuilder.BuildSearchUrl(
                input["base_url"]!.GetValue<string>(),
                input["category"]!.GetValue<string>(),
                input["max_results"]?.GetValue<int>() ?? input["default_max_results"]!.GetValue<int>(),
                Date(input["from"]),
                Date(input["to"]));

        url.ShouldBe(testCase["expected"]!.GetValue<string>());
    }

    [Fact]
    public void Lookup_by_id_strips_only_a_version_suffix() => // B8: Python's split("v") turned this into "sol"
        ArxivQueryBuilder.BuildIdUrl("https://export.arxiv.org/api/query", "solv-int/9901001v2")
            .ShouldBe("https://export.arxiv.org/api/query?id_list=solv-int%2F9901001&max_results=1");

    [Fact]
    public void A_recorded_feed_parses_like_python()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "arxiv", "feed-cs-ai-20260930.xml"));
        var expected = ParityFixtures.Load("arxiv/parsed-feed.json").AsArray();

        var papers = ArxivAtomParser.Parse(xml);

        papers.Count.ShouldBe(expected.Count);
        for (var i = 0; i < papers.Count; i++)
        {
            var e = expected[i]!;
            var paper = papers[i];
            (paper.ArxivId, paper.Title, paper.Abstract, paper.PdfUrl).ShouldBe((
                e["arxiv_id"]!.GetValue<string>(), e["title"]!.GetValue<string>(),
                e["abstract"]!.GetValue<string>(), e["pdf_url"]!.GetValue<string>()));
            paper.Authors.ShouldBe(e["authors"]!.AsArray().Select(a => a!.GetValue<string>()));
            paper.Categories.ShouldBe(e["categories"]!.AsArray().Select(c => c!.GetValue<string>()));
            paper.PublishedDate.ShouldBe(DateTimeOffset.Parse(e["published_date"]!.GetValue<string>(), CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public void Entries_are_cleaned_and_bad_ones_skipped()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>http://arxiv.org/abs/2610.00001v2</id>
                <title>
                  A Title
                  On Two Lines
                </title>
                <summary> An abstract. </summary>
                <published>2026-10-01T12:00:00Z</published>
                <author><name> Ada Lovelace </name></author>
                <author><name></name></author>
                <category term="cs.AI"/><category term="cs.LG"/>
                <link href="http://arxiv.org/pdf/2610.00001v2" type="application/pdf"/>
              </entry>
              <entry><title>No id, skipped</title></entry>
              <entry><id>http://arxiv.org/abs/2610.00003v1</id><published>not a date</published></entry>
              <entry><id>http://arxiv.org/abs/2610.00004v1</id><published>2026-10-01T00:00:00Z</published></entry>
            </feed>
            """;

        var papers = ArxivAtomParser.Parse(xml);

        papers.Select(p => p.ArxivId).ShouldBe(["2610.00001v2", "2610.00004v1"]);
        var first = papers[0];
        first.Title.ShouldBe("A Title       On Two Lines"); // newlines become spaces and the indentation stays, as in Python
        (first.Abstract, first.PdfUrl).ShouldBe(("An abstract.", "https://arxiv.org/pdf/2610.00001v2"));
        first.Authors.ShouldBe(["Ada Lovelace"]);
        first.Categories.ShouldBe(["cs.AI", "cs.LG"]);
        (papers[1].Title, papers[1].PdfUrl, papers[1].Authors.Count).ShouldBe(("", "", 0));
    }

    [Fact]
    public void Malformed_xml_is_an_api_error() =>
        Should.Throw<ArxivApiException>(() => ArxivAtomParser.Parse("<feed><entry>")).Message
            .ShouldStartWith("Failed to parse arXiv XML response");

    private static DateOnly? Date(JsonNode? value) =>
        value is null ? null : DateOnly.ParseExact(value.GetValue<string>(), "yyyyMMdd", CultureInfo.InvariantCulture);
}

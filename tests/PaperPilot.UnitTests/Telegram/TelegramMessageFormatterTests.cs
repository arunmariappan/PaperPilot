using PaperPilot.Api.Telegram;
using PaperPilot.Core.Contracts;
using static PaperPilot.UnitTests.Rag.FakeRetriever;

namespace PaperPilot.UnitTests.Telegram;

public sealed class TelegramMessageFormatterTests
{
    private static readonly string[] SearchHitIds =
        ["2610.00001v1", "2610.00001v1", "2610.00002v2", "", "2610.00003v1", "2610.00004v1", "2610.00005v1", "2610.00006v1"];

    [Fact]
    public void An_answer_lists_up_to_five_sources_as_abstract_pages()
    {
        var response = Response("Attention helps [arXiv:2610.00001].",
        [
            "https://arxiv.org/pdf/2610.00001.pdf",
            "https://arxiv.org/pdf/2610.00002.pdf",
            "https://arxiv.org/pdf/cs/0112017.pdf", // B8: Python's split("/") kept only "0112017"
            "https://arxiv.org/pdf/2610.00004.pdf",
            "https://arxiv.org/pdf/2610.00005.pdf",
            "https://arxiv.org/pdf/2610.00006.pdf",
        ]);

        TelegramMessageFormatter.Answer(response).ShouldBe(
            "*Answer:*\nAttention helps [arXiv:2610.00001].\n\n*Sources:*\n"
            + "1. https://arxiv.org/abs/2610.00001\n2. https://arxiv.org/abs/2610.00002\n3. https://arxiv.org/abs/cs/0112017\n"
            + "4. https://arxiv.org/abs/2610.00004\n5. https://arxiv.org/abs/2610.00005\n");
    }

    [Fact]
    public void An_answer_without_sources_has_no_sources_section() =>
        TelegramMessageFormatter.Answer(Response("No idea.", [])).ShouldBe("*Answer:*\nNo idea.\n");

    [Fact]
    public void An_agent_answer_adds_the_reasoning_steps() // N5
    {
        var response = new AgenticAskResponse
        {
            Query = "q",
            Answer = "Yes.",
            Sources = ["https://arxiv.org/pdf/2610.00001.pdf"],
            ChunksUsed = 1,
            SearchMode = "hybrid",
            ReasoningSteps = ["Validated query scope (score: 90/100)", "Generated answer from context"],
            RetrievalAttempts = 1,
        };

        TelegramMessageFormatter.AgentAnswer(response).ShouldBe(
            "*Answer:*\nYes.\n\n*Sources:*\n1. https://arxiv.org/abs/2610.00001\n\n*Reasoning:*\n"
            + "- Validated query scope (score: 90/100)\n- Generated answer from context\n");
    }

    [Fact]
    public void Search_results_list_the_first_five_distinct_papers()
    {
        var hits = SearchHitIds.Select((id, i) => Chunk(id, i, "Text.") with { Title = $"Paper {id}" }).ToList();

        TelegramMessageFormatter.SearchResults(hits).ShouldBe(
            "Found 5 papers:\n\n"
            + "1. Paper 2610.00001v1\nhttps://arxiv.org/abs/2610.00001\n\n"
            + "2. Paper 2610.00002v2\nhttps://arxiv.org/abs/2610.00002\n\n"
            + "3. Paper 2610.00003v1\nhttps://arxiv.org/abs/2610.00003\n\n"
            + "4. Paper 2610.00004v1\nhttps://arxiv.org/abs/2610.00004\n\n"
            + "5. Paper 2610.00005v1\nhttps://arxiv.org/abs/2610.00005\n\n");
        TelegramMessageFormatter.SearchResults([]).ShouldBe("No papers found. Try different keywords.");
    }

    [Fact]
    public void A_long_message_is_split_at_paragraph_breaks() // B19
    {
        var paragraphs = Enumerable.Range(0, 9).Select(i => new string((char)('a' + i), 999)).ToList();
        var text = string.Join("\n\n", paragraphs); // 9,007 characters

        var parts = TelegramMessageFormatter.Split(text);

        parts.Count.ShouldBe(3);
        parts.ShouldAllBe(p => p.Length <= 4096);
        parts.ShouldBe([
            string.Join("\n\n", paragraphs[..4]),
            string.Join("\n\n", paragraphs[4..8]),
            paragraphs[8],
        ]);
    }

    [Fact]
    public void Without_paragraph_breaks_it_splits_at_line_breaks_then_anywhere()
    {
        TelegramMessageFormatter.Split("aaaa\nbbbb\ncc", maxLength: 10).ShouldBe(["aaaa\nbbbb", "cc"]);
        TelegramMessageFormatter.Split("abcdefghij", maxLength: 4).ShouldBe(["abcd", "efgh", "ij"]);
        TelegramMessageFormatter.Split("ab😀cd", maxLength: 3).ShouldBe(["ab", "😀c", "d"]); // the emoji isn't cut in half
        TelegramMessageFormatter.Split("short").ShouldBe(["short"]);
        TelegramMessageFormatter.Split("aaaa\n\n\n\nbbbb", maxLength: 5).ShouldBe(["aaaa", "bbbb"]); // no blank parts
    }

    private static AskResponse Response(string answer, IReadOnlyList<string> sources) => new()
    {
        Query = "q",
        Answer = answer,
        Sources = sources,
        ChunksUsed = sources.Count,
        SearchMode = "hybrid",
    };
}

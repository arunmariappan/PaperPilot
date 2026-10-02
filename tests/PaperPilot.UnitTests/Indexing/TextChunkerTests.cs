using System.Text.Json.Nodes;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Indexing;
using PaperPilot.Core.Options;
using PaperPilot.Core.Text;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Indexing;

public sealed class TextChunkerTests
{
    private static readonly JsonArray Cases = ParityFixtures.Load("chunking/chunks.json").AsArray();

    private static readonly TextChunker Chunker = new(new ChunkingOptions());

    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c!["name"]!.GetValue<string>())];

    [Fact]
    public void All_python_cases_are_covered() => Cases.Count.ShouldBe(9);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Chunks_match_python(string name)
    {
        var testCase = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var input = testCase["input"]!;
        var sections = input["sections"]?.AsArray().Select(s => new PaperSection(
            (s!["title"] ?? s["heading"])!.GetValue<string>(), (s["content"] ?? s["text"])!.GetValue<string>())).ToList();

        var chunks = Chunker.ChunkPaper(
            input["title"]!.GetValue<string>(),
            input["abstract"]!.GetValue<string>(),
            input["full_text"]!.GetValue<string>(),
            sections);

        var expected = testCase["expected"]!.AsArray();
        chunks.Count.ShouldBe(expected.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var e = expected[i]!;
            chunks[i].ShouldBe(new TextChunk(
                e["text"]!.GetValue<string>(),
                e["chunk_index"]!.GetValue<int>(),
                e["start_char"]!.GetValue<int>(),
                e["end_char"]!.GetValue<int>(),
                e["word_count"]!.GetValue<int>(),
                e["section_title"]?.GetValue<string>()), $"chunk {i}");
        }
    }

    [Fact]
    public void A_text_shorter_than_the_minimum_is_one_chunk() // B9: Python raised TypeError
    {
        var chunks = Chunker.ChunkText("  A short\ttext \n of six words.  ");

        chunks.ShouldHaveSingleItem().ShouldBe(new TextChunk("A short text of six words.", 0, 0, 32, 6, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t \u001c ")]
    public void A_blank_text_has_no_chunks(string? text) => Chunker.ChunkText(text).ShouldBeEmpty();

    [Fact]
    public void Small_sections_are_combined_with_real_newlines() // B10: Python joined them with a literal backslash-n
    {
        var chunks = Chunker.ChunkPaper("T", Words(150, "abstract"), null,
        [
            new PaperSection("Section One", Words(60, "one")),
            new PaperSection("Section Two", Words(60, "two")),
            new PaperSection("Section Three", Words(60, "three")),
            new PaperSection("Section Four", Words(60, "four")),
        ]);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.SectionTitle.ShouldBe("Section One + Section Two + Section Three + 1 more");
        chunk.Text.ShouldContain($"{Words(60, "one")}\n\nSection: Section Two\n\n");
        chunk.Text.ShouldNotContain("\\n");
        chunk.WordCount.ShouldBe(PythonText.Split(chunk.Text).Length);
    }

    [Fact]
    public void A_short_buffer_of_small_sections_is_merged_into_the_previous_chunk() // B10
    {
        var chunks = Chunker.ChunkPaper("T", Words(20, "abstract"), null,
        [
            new PaperSection("Introduction", Words(150, "intro")),
            new PaperSection("Closing Remarks", Words(30, "closing")),
        ]);

        var chunk = chunks.ShouldHaveSingleItem();
        chunk.SectionTitle.ShouldBe("Introduction + Combined");
        chunk.Text.ShouldEndWith($"{Words(150, "intro")}\n\nSection: Closing Remarks\n\n{Words(30, "closing")}");
        (chunk.ChunkIndex, chunk.StartChar, chunk.EndChar).ShouldBe((0, 0, chunk.Text.Length));
    }

    [Fact]
    public void Sections_that_share_a_title_are_all_kept() // B31: Python's dict kept only the last one
    {
        var chunks = Chunker.ChunkPaper("T", Words(20, "abstract"), null,
        [
            new PaperSection("Proof", Words(150, "first")),
            new PaperSection("Lemma Statement", Words(150, "middle")),
            new PaperSection("Proof", Words(150, "second")),
        ]);

        chunks.Select(c => c.SectionTitle).ShouldBe(["Proof", "Lemma Statement", "Proof"]);
        chunks[0].Text.ShouldContain("first");
        chunks[2].Text.ShouldContain("second");
    }

    [Fact]
    public void Section_and_window_sizes_come_from_options() // B11
    {
        var chunker = new TextChunker(new ChunkingOptions
        {
            ChunkSize = 10,
            OverlapSize = 2,
            MinChunkSize = 3,
            SectionMinWords = 5,
            SectionMaxWords = 20,
        });

        chunker.ChunkText(Words(26, "w")).Select(c => (c.StartChar, c.WordCount)).ShouldBe([(0, 10), (23, 10), (54, 10)]);
        chunker.ChunkPaper("T", "An abstract.", null, [new PaperSection("Section A", Words(30, "a"))])
            .Select(c => c.SectionTitle).ShouldBe(["Section A (Part 1)", "Section A (Part 2)", "Section A (Part 3)", "Section A (Part 4)"]);
    }

    [Fact]
    public void Section_chunking_can_be_switched_off() // B11: Python never read CHUNKING__SECTION_BASED
    {
        var chunker = new TextChunker(new ChunkingOptions { SectionBased = false });

        var chunks = chunker.ChunkPaper("T", "An abstract.", Words(700, "raw"), [new PaperSection("Introduction", Words(300, "s"))]);

        chunks.Select(c => c.SectionTitle).ShouldBe([null, null]);
    }

    /// <summary>"w1 w2 … wn" with the given prefix.</summary>
    private static string Words(int count, string prefix) =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => $"{prefix}{i}"));
}

using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PaperPilot.Core.Search;
using PaperPilot.Rag.Prompts;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Rag;

/// <summary>Golden tests: system + user message must equal Python's <c>create_rag_prompt</c> output.</summary>
public sealed class RagPromptBuilderTests
{
    private static readonly JsonArray Cases = ParityFixtures.Load("rag/prompts.json").AsArray();

    public static TheoryData<string> CaseNames => [.. Cases.Select(c => c!["name"]!.GetValue<string>())];

    [Fact]
    public void All_python_cases_are_covered() => Cases.Count.ShouldBe(3);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Prompt_matches_python(string name)
    {
        var testCase = Cases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var chunks = testCase["input"]!["chunks"]!.AsArray()
            .Select(c => Chunk(c!["arxiv_id"]!.GetValue<string>(), c["chunk_text"]!.GetValue<string>()))
            .ToList();

        var prompt = RagPromptBuilder.Build(testCase["input"]!["query"]!.GetValue<string>(), chunks);

        prompt.Combined.ShouldBe(testCase["expected"]!.GetValue<string>());
    }

    [Fact]
    public void The_system_prompt_is_the_system_message_and_the_rest_is_the_user_message() // C3
    {
        var messages = RagPromptBuilder.Build("What are transformers?", [Chunk("2610.00001v1", "Text.")]).ToMessages();

        messages.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User]);
        messages[0].Text.ShouldStartWith("You are an AI assistant specialized in answering questions about academic papers");
        messages[0].Text.ShouldEndWith("NEVER add introductory phrases or explanations before your JSON response");
        messages[1].Text.ShouldStartWith("### Context from Papers:\n\n[1. arXiv:2610.00001v1]\nText.\n\n### Question:");
    }

    [Fact]
    public void A_chunk_without_text_falls_back_to_the_abstract() =>
        RagPromptBuilder.Build("q", [Chunk("2610.00001v1", null) with { Abstract = "The abstract." }]).User
            .ShouldContain("[1. arXiv:2610.00001v1]\nThe abstract.\n\n");

    internal static ChunkHit Chunk(string arxivId, string? text, int index = 0) =>
        new($"{arxivId}:{index}", arxivId, "Title", null, null, null, text, index, null, 1.0, null);
}

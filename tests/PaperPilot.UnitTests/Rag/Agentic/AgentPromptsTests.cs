using System.Text.Json.Nodes;
using PaperPilot.Rag.Agentic;
using PaperPilot.UnitTests.TestSupport;
using static PaperPilot.UnitTests.Rag.FakeRetriever;

namespace PaperPilot.UnitTests.Rag.Agentic;

/// <summary>Golden tests against <c>agentic/prompts.json</c>, recorded from Python's prompts.</summary>
public sealed class AgentPromptsTests
{
    private static readonly JsonArray PromptCases = ParityFixtures.Load("agentic/prompts.json").AsArray();

    public static TheoryData<string> PromptCaseNames => [.. PromptCases.Select(c => c!["name"]!.GetValue<string>())];

    [Fact]
    public void All_python_cases_are_covered() => PromptCases.Count.ShouldBe(3);

    [Theory]
    [MemberData(nameof(PromptCaseNames))]
    public void Prompts_match_python(string name) // braces in a question or a paper stay as they are
    {
        var testCase = PromptCases.Single(c => c!["name"]!.GetValue<string>() == name)!;
        var question = testCase["input"]!["question"]!.GetValue<string>();
        var context = testCase["input"]!["context"]!.GetValue<string>();
        var expected = testCase["expected"]!;

        AgentPrompts.Guardrail(question).ShouldBe(expected["guardrail"]!.GetValue<string>());
        AgentPrompts.GradeDocuments(question, context).ShouldBe(expected["grade_documents"]!.GetValue<string>());
        AgentPrompts.Rewrite(question).ShouldBe(expected["rewrite"]!.GetValue<string>());
        AgentPrompts.GenerateAnswer(question, context).ShouldBe(expected["generate_answer"]!.GetValue<string>());
    }

    [Fact]
    public void Context_lists_numbered_excerpts_with_id_and_title() // B4
    {
        var retrieval = Retrieved(
            Chunk("2610.00001v1", 0, "Self-attention."),
            Chunk("2610.00002v2", 3, "Reward models.") with { Title = "RLHF" },
            Chunk("2610.00003v1", 0, null!) with { Abstract = "Only an abstract." });

        AgentContextFormatter.Format(retrieval).ShouldBe(
            "[1] arXiv:2610.00001v1 — Title\nSelf-attention.\n\n"
            + "[2] arXiv:2610.00002v2 — RLHF\nReward models.\n\n"
            + "[3] arXiv:2610.00003v1 — Title\nOnly an abstract.");
        AgentContextFormatter.Format(Retrieved()).ShouldBeEmpty();
    }
}

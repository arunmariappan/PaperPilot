using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Agentic.Executors;
using PaperPilot.UnitTests.TestSupport;
using static PaperPilot.UnitTests.Rag.FakeRetriever;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PaperPilot.UnitTests.Rag.Agentic;

/// <summary>
/// The agent's nodes on their own: fallbacks compared with <c>agentic/fallbacks.json</c> (recorded from Python's nodes
/// with a failing LLM), and the structured-output schemas.
/// </summary>
public sealed class AgentNodeTests : IDisposable
{
    private static readonly JsonNode Python = ParityFixtures.Load("agentic/fallbacks.json");

    private readonly ScriptedChatClient _chat = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Question => Python["question"]!.GetValue<string>();

    private static string Error => Python["error"]!.GetValue<string>();

    [Fact]
    public async Task Guardrail_fallback_matches_python()
    {
        _chat.Script(AgentCall.Guardrail, new InvalidOperationException(Error));
        var guardrail = new GuardrailExecutor(_chat, ChatOptions, Agentic, NullLogger<GuardrailExecutor>.Instance);

        var state = await guardrail.HandleAsync(State(), null!, Ct);

        state.Guardrail.ShouldBe(new GuardrailScoring(
            Python["guardrail"]!["score"]!.GetValue<int>(), Python["guardrail"]!["reason"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Grading_fallback_matches_python(int index)
    {
        var expected = Python["grading"]![index]!;
        _chat.Script(AgentCall.Grade, new InvalidOperationException(Error));
        var grade = new GradeExecutor(_chat, ChatOptions, Agentic, NullLogger<GradeExecutor>.Instance);

        // The heuristic sees the formatted context (B4), which adds "[1] arXiv:… — Title\n" to the chunk text.
        var state = await grade.HandleAsync(
            State() with { LastRetrieval = Retrieved(Chunk("2610.00001v1", 0, expected["context"]!.GetValue<string>())) }, null!, Ct);

        var grading = state.Gradings.ShouldHaveSingleItem();
        (grading.IsRelevant, grading.Reasoning).ShouldBe(
            (expected["is_relevant"]!.GetValue<bool>(), expected["reasoning"]!.GetValue<string>()));
        grading.DocumentId.ShouldBe("retrieved_docs");
    }

    [Fact]
    public async Task Rewrite_fallback_matches_python()
    {
        _chat.Script(AgentCall.Rewrite, new InvalidOperationException(Error));
        var rewrite = new RewriteExecutor(_chat, ChatOptions, Agentic, NullLogger<RewriteExecutor>.Instance);

        var state = await rewrite.HandleAsync(State(), null!, Ct);

        (state.CurrentQuery, state.RewrittenQuery).ShouldBe(
            (Python["rewritten_query"]!.GetValue<string>(), Python["rewritten_query"]!.GetValue<string>()));
    }

    [Fact]
    public void Fixed_answers_match_python()
    {
        AgentMessages.OutOfScope(Question).ShouldBe(Python["out_of_scope_answer"]!.GetValue<string>());
        AgentMessages.MaxAttempts(2).ShouldBe(Python["max_attempts_answer"]!.GetValue<string>());
        AgentMessages.GenerationFailed(Error).ShouldBe(Python["generated_answer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Structured_calls_ask_for_a_json_schema_with_pythons_field_names()
    {
        _chat.Script(AgentCall.Grade, """{"binary_score": "no", "reasoning": "Off topic."}""");
        var options = MsOptions.Create(new AgenticRagOptions());
        await new GuardrailExecutor(_chat, ChatOptions, options, NullLogger<GuardrailExecutor>.Instance).HandleAsync(State(), null!, Ct);
        await new GradeExecutor(_chat, ChatOptions, options, NullLogger<GradeExecutor>.Instance)
            .HandleAsync(State() with { LastRetrieval = Retrieved(Chunk("2610.00001v1", 0, "Text.")) }, null!, Ct);
        await new RewriteExecutor(_chat, ChatOptions, options, NullLogger<RewriteExecutor>.Instance).HandleAsync(State(), null!, Ct);

        Schema(AgentCall.Guardrail).ShouldBe(("score:integer reason:string", "score reason"));
        Schema(AgentCall.Grade).ShouldBe(("binary_score:string reasoning:string", "binary_score"));
        Schema(AgentCall.Rewrite).ShouldBe(("rewritten_query:string reasoning:string", "rewritten_query reasoning"));
        var grade = (ChatResponseFormatJson)_chat.Calls.Single(c => c.Call == AgentCall.Grade).Options!.ResponseFormat!;
        grade.Schema!.Value.GetProperty("properties").GetProperty("binary_score").GetProperty("enum").EnumerateArray()
            .Select(v => v.GetString()).ShouldBe(["yes", "no"]);
    }

    public void Dispose() => _chat.Dispose();

    private static ChatOptionsFactory ChatOptions => new(MsOptions.Create(new OllamaOptions()));

    private static Microsoft.Extensions.Options.IOptions<AgenticRagOptions> Agentic => MsOptions.Create(new AgenticRagOptions());

    private static AgentRunState State() => new()
    {
        OriginalQuestion = Question,
        CurrentQuery = Question,
        Model = "qwen3.5:9b",
        TopK = 3,
        UseHybrid = true,
    };

    /// <summary>The schema's properties as <c>name:type</c>, and its required properties.</summary>
    private (string Properties, string Required) Schema(AgentCall call)
    {
        var format = (ChatResponseFormatJson)_chat.Calls.Single(c => c.Call == call).Options!.ResponseFormat!;
        var schema = format.Schema!.Value;
        var properties = schema.GetProperty("properties").EnumerateObject()
            .Select(p => $"{p.Name}:{p.Value.GetProperty("type").GetString()}");
        var required = schema.TryGetProperty("required", out var names) ? names.EnumerateArray().Select(n => n.GetString()) : [];
        return (string.Join(' ', properties), string.Join(' ', required));
    }
}

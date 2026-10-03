using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperPilot.Rag.Agentic;

/// <summary>The guardrail's structured output: how clearly the question is about CS/AI/ML research.</summary>
internal sealed record GuardrailScoring(
    [property: Description("Relevance score between 0 and 100")] int Score,
    [property: Description("Brief reason for the score")] string Reason);

/// <summary>The grader's verdict, <c>yes</c> or <c>no</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BinaryScore>))]
internal enum BinaryScore
{
    [JsonStringEnumMemberName("yes")]
    Yes,

    [JsonStringEnumMemberName("no")]
    No,
}

/// <summary>The grader's structured output.</summary>
internal sealed record GradeDocuments(
    [property: Description("Document relevance: 'yes' or 'no'")] BinaryScore BinaryScore,
    [property: Description("Explanation for the decision")] string Reasoning = "");

/// <summary>The rewriter's structured output.</summary>
internal sealed record QueryRewriteOutput(
    [property: Description("The improved query optimized for document retrieval")] string RewrittenQuery,
    [property: Description("Brief explanation of how the query was improved")] string Reasoning);

/// <summary>One grading of a retrieval, as Python recorded it (<c>document_id</c> is always <c>retrieved_docs</c>).</summary>
internal sealed record GradingResult(string DocumentId, bool IsRelevant, double Score, string Reasoning);

/// <summary>
/// JSON settings for the structured outputs: snake_case names as the prompts ask for, and, like pydantic, a missing
/// or null required field is an error rather than a default value (which then triggers the node's fallback).
/// </summary>
internal static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

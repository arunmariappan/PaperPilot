using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// Grades whether the retrieved chunks are relevant to the user's original question (B27). A retrieval that found
/// nothing isn't sent to the LLM. If the LLM fails, any context longer than 50 characters counts as relevant.
/// </summary>
internal sealed partial class GradeExecutor(
    IChatClient chat, ChatOptionsFactory chatOptions, IOptions<AgenticRagOptions> options, ILogger<GradeExecutor> logger)
    : AgentNode("grade_documents")
{
    internal const string DocumentId = "retrieved_docs";

    private const int PreviewLength = 500;

    public override async ValueTask<AgentRunState> HandleAsync(
        AgentRunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var documents = message.LastRetrieval is { } retrieval ? AgentContextFormatter.Format(retrieval) : string.Empty;
        var maxAttempts = options.Value.MaxRetrievalAttempts;

        using var span = RagTelemetry.Source.StartActivity("document_grading");
        span.SetInput(new
        {
            Query = message.OriginalQuestion,
            ContextLength = documents.Length,
            HasContext = documents.Length > 0,
            ChunksReceived = documents.Length == 0
                ? []
                : new object[] { new { TextPreview = RagTelemetry.Preview(documents, PreviewLength), Length = documents.Length } },
        });
        span.SetMetadata(("node", "grade_documents"), ("model", message.Model));

        if (documents.Length == 0)
        {
            var nothing = message with { DocumentsRelevant = false };
            span.SetOutput(new { RoutingDecision = AgentRouting.Name(AgentRouting.AfterGrading(nothing, maxAttempts)), Reason = "no_context" });
            return nothing;
        }

        GradingResult grading;
        try
        {
            var response = await chat.GetResponseAsync<GradeDocuments>(
                AgentPrompts.GradeDocuments(message.OriginalQuestion, documents),
                AgentJson.Options,
                chatOptions.Create(message.Model, options.Value.GradingTemperature),
                cancellationToken: cancellationToken);
            var grade = response.Result;
            var relevant = grade.BinaryScore == BinaryScore.Yes;
            grading = new GradingResult(DocumentId, relevant, relevant ? 1.0 : 0.0, grade.Reasoning);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogGradingFailed(logger, ex);
            var relevant = documents.Trim().Length > 50;
            grading = new GradingResult(
                DocumentId,
                relevant,
                relevant ? 1.0 : 0.0,
                $"Fallback heuristic (LLM failed): {(relevant ? "sufficient content" : "insufficient content")}");
            span.Degrade(ex, ObservationLevels.Warning);
        }

        var state = message with { Gradings = [.. message.Gradings, grading], DocumentsRelevant = grading.IsRelevant };
        span.SetOutput(new
        {
            RoutingDecision = AgentRouting.Name(AgentRouting.AfterGrading(state, maxAttempts)),
            grading.IsRelevant,
            grading.Score,
            grading.Reasoning,
        });
        return state;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM grading failed, falling back to the length heuristic")]
    private static partial void LogGradingFailed(ILogger logger, Exception exception);
}

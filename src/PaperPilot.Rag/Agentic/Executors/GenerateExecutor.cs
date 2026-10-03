using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// Answers the user's original question (B27) from the graded chunks. If the LLM fails, the answer is an apology
/// that includes the error.
/// </summary>
internal sealed partial class GenerateExecutor(
    IChatClient chat, ChatOptionsFactory chatOptions, IOptions<AgenticRagOptions> options, ILogger<GenerateExecutor> logger)
    : AgentEndNode("generate_answer")
{
    private const int PreviewLength = 1000;

    protected override async ValueTask<AgentRunState> CompleteAsync(AgentRunState state, CancellationToken cancellationToken)
    {
        var documents = state.LastRetrieval is { } retrieval ? AgentContextFormatter.Format(retrieval) : string.Empty;
        if (documents.Length == 0)
        {
            documents = AgentMessages.NoDocuments;
        }

        var sourcesCount = state.LastRetrieval?.Sources.Count ?? 0;
        var temperature = options.Value.GenerateTemperature;

        using var span = RagTelemetry.Source.StartActivity("answer_generation");
        span.SetInput(new
        {
            Query = state.OriginalQuestion,
            ContextLength = documents.Length,
            SourcesCount = sourcesCount,
            ChunksUsed = new[] { new { TextPreview = RagTelemetry.Preview(documents, PreviewLength), Length = documents.Length } },
        });
        span.SetMetadata(("node", "generate_answer"), ("model", state.Model), ("temperature", temperature));

        string answer;
        try
        {
            var response = await chat.GetResponseAsync(
                AgentPrompts.GenerateAnswer(state.OriginalQuestion, documents),
                chatOptions.Create(state.Model, temperature),
                cancellationToken);
            answer = response.Text;
            span.SetOutput(new { AnswerLength = answer.Length, SourcesUsed = sourcesCount });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogGenerationFailed(logger, ex);
            answer = AgentMessages.GenerationFailed(ex.Message);
            span.Degrade(ex, ObservationLevels.Error);
            span.SetOutput(new { Error = ex.Message, Fallback = true });
        }

        return state with { Answer = answer, Ending = AgentEnding.Answered };
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "LLM answer generation failed, falling back to an error message")]
    private static partial void LogGenerationFailed(ILogger logger, Exception exception);
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic;

/// <summary>Agentic RAG for <c>/ask-agentic</c>.</summary>
public interface IAgenticRagService
{
    /// <summary>
    /// Runs guardrail → retrieve → grade → (rewrite → retrieve)* → answer. LLM failures fall back to defaults and a
    /// search outage ends with an explicit answer, so this only throws for unexpected errors.
    /// </summary>
    /// <exception cref="ArgumentException">The query is blank.</exception>
    Task<AgenticAskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken = default);
}

internal sealed partial class AgenticRagService(
    AgenticRagWorkflow workflow,
    ChatOptionsFactory chatOptions,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<AgenticRagService> logger) : IAgenticRagService
{
    private const string RequestSpanName = "agentic_rag_request";

    public async Task<AgenticAskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Query cannot be empty", nameof(request));
        }

        var model = chatOptions.ResolveModel(request.Model);
        using var activity = RagTelemetry.StartRequest(RequestSpanName, request.Query);
        (string, object?)[] metadata =
        [
            ("env", environment.EnvironmentName),
            ("service", "agentic_rag"),
            ("top_k", request.TopK),
            ("use_hybrid", request.UseHybrid),
            ("model", model),
        ];
        activity.SetMetadata(metadata);
        activity.SetTraceMetadata(metadata);
        var started = time.GetTimestamp();

        try
        {
            var state = await workflow.RunAsync(
                new AgentRunState
                {
                    OriginalQuestion = request.Query,
                    CurrentQuery = request.Query,
                    Model = model,
                    TopK = request.TopK,
                    UseHybrid = request.UseHybrid,
                    Categories = request.Categories,
                },
                cancellationToken);

            var response = new AgenticAskResponse
            {
                Query = request.Query,
                Answer = state.Answer ?? "No answer generated.",
                Sources = state.Ending == AgentEnding.Answered ? state.LastRetrieval?.Sources ?? [] : [],
                ChunksUsed = state.LastRetrieval?.Chunks.Count ?? 0,
                SearchMode = state.LastRetrieval?.SearchMode ?? (request.UseHybrid ? SearchModes.Hybrid : SearchModes.Bm25),
                ReasoningSteps = ReasoningSteps(state),
                RetrievalAttempts = state.RetrievalAttempts,
                TraceId = activity?.TraceId.ToHexString(),
            };

            var elapsed = time.GetElapsedTime(started);
            activity.SetTraceOutput(new
            {
                response.Answer,
                SourcesCount = response.Sources.Count,
                response.RetrievalAttempts,
                response.ReasoningSteps,
                ExecutionTime = Math.Round(elapsed.TotalSeconds, 3),
            });
            LogCompleted(logger, state.Ending, state.RetrievalAttempts, elapsed.TotalSeconds);
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, ex);
            activity.Fail(ex);
            activity?.SetTag(LangfuseAttributes.ObservationLevel, ObservationLevels.Error);
            activity.SetTraceOutput(new { Error = ex.Message });
            throw;
        }
    }

    /// <summary>
    /// The steps Python reported, word for word, except the last one, which says how the run actually ended (B28).
    /// </summary>
    internal static IReadOnlyList<string> ReasoningSteps(AgentRunState state)
    {
        List<string> steps = [];
        if (state.Guardrail is { } guardrail)
        {
            steps.Add($"Validated query scope (score: {guardrail.Score}/100)");
        }

        if (state.RetrievalAttempts > 0)
        {
            steps.Add($"Retrieved documents ({state.RetrievalAttempts} attempt(s))");
        }

        if (state.Gradings.Count > 0)
        {
            steps.Add($"Graded documents ({state.Gradings.Count(g => g.IsRelevant)} relevant)");
        }

        if (state.RewrittenQuery is not null)
        {
            steps.Add("Rewritten query for better results");
        }

        steps.Add(state.Ending switch
        {
            AgentEnding.OutOfScope => "Responded as out of scope",
            AgentEnding.MaxAttempts => $"Stopped after {state.RetrievalAttempts} retrieval attempts",
            AgentEnding.SearchUnavailable => "Search was unavailable",
            _ => "Generated answer from context",
        });
        return steps;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agentic RAG ended with {Ending} after {Attempts} retrieval attempt(s) in {Seconds:F2}s")]
    private static partial void LogCompleted(ILogger logger, AgentEnding? ending, int attempts, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in agentic RAG execution")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// Rewrites the original question into a better search query for the next retrieval. If the LLM fails or returns
/// a blank query, it appends search keywords to the question instead.
/// </summary>
internal sealed partial class RewriteExecutor(
    IChatClient chat, ChatOptionsFactory chatOptions, IOptions<AgenticRagOptions> options, ILogger<RewriteExecutor> logger)
    : AgentNode("rewrite_query")
{
    internal const string FallbackReasoning = "Fallback: Simple keyword expansion due to LLM error";

    public override async ValueTask<AgentRunState> HandleAsync(
        AgentRunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var original = message.OriginalQuestion;

        using var span = RagTelemetry.Source.StartActivity("query_rewriting");
        span.SetInput(new { OriginalQuery = original, Attempt = message.RetrievalAttempts });
        span.SetMetadata(("node", "rewrite_query"), ("strategy", "llm_based_expansion"), ("model", message.Model));

        string rewritten;
        string reasoning;
        try
        {
            var response = await chat.GetResponseAsync<QueryRewriteOutput>(
                AgentPrompts.Rewrite(original),
                AgentJson.Options,
                chatOptions.Create(message.Model, options.Value.RewriteTemperature),
                cancellationToken: cancellationToken);
            var output = response.Result;
            rewritten = output.RewrittenQuery.Trim();
            if (rewritten.Length == 0)
            {
                throw new InvalidOperationException("LLM returned empty rewritten query");
            }

            reasoning = output.Reasoning;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogRewriteFailed(logger, ex);
            rewritten = Fallback(original);
            reasoning = FallbackReasoning;
            span.Degrade(ex, ObservationLevels.Warning);
        }

        span.SetOutput(new { RewrittenQuery = rewritten, Reasoning = reasoning, OriginalQuery = original });
        return message with { CurrentQuery = rewritten, RewrittenQuery = rewritten };
    }

    internal static string Fallback(string question) => $"{question} research paper arxiv machine learning";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to rewrite the query with the LLM, falling back to keyword expansion")]
    private static partial void LogRewriteFailed(ILogger logger, Exception exception);
}

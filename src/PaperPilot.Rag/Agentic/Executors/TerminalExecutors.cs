using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>Declines a question the guardrail scored below the threshold. No LLM call.</summary>
internal sealed class OutOfScopeExecutor() : AgentEndNode("out_of_scope")
{
    protected override ValueTask<AgentRunState> CompleteAsync(AgentRunState state, CancellationToken cancellationToken) =>
        ValueTask.FromResult(state with
        {
            Answer = AgentMessages.OutOfScope(state.OriginalQuestion),
            Ending = AgentEnding.OutOfScope,
        });
}

/// <summary>Gives up after the last retrieval attempt found nothing relevant.</summary>
internal sealed class MaxAttemptsExecutor(IOptions<AgenticRagOptions> options) : AgentEndNode("max_attempts")
{
    protected override ValueTask<AgentRunState> CompleteAsync(AgentRunState state, CancellationToken cancellationToken) =>
        ValueTask.FromResult(state with
        {
            Answer = AgentMessages.MaxAttempts(options.Value.MaxRetrievalAttempts),
            Ending = AgentEnding.MaxAttempts,
        });
}

/// <summary>Ends the run when search can't be reached (B6).</summary>
internal sealed class SearchUnavailableExecutor() : AgentEndNode("search_unavailable")
{
    protected override ValueTask<AgentRunState> CompleteAsync(AgentRunState state, CancellationToken cancellationToken) =>
        ValueTask.FromResult(state with { Answer = AgentMessages.SearchUnavailable, Ending = AgentEnding.SearchUnavailable });
}

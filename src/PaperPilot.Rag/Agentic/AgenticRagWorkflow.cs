using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Rag.Agentic.Executors;

namespace PaperPilot.Rag.Agentic;

/// <summary>
/// The agent graph as a Microsoft Agent Framework workflow, built once and shared by concurrent requests:
/// <code>
/// guardrail ─ in scope ─→ retrieve ─→ grade_documents ─ relevant ─→ generate_answer
///     │                     ↑   │            ├─ not relevant, attempts left ─→ rewrite_query ─┐
///     │                     │   │            └─ not relevant, no attempts left ─→ max_attempts │
///     │                     │   └─ search unavailable ─→ search_unavailable                    │
///     │                     └──────────────────────────────────────────────────────────────────┘
///     └─ out of scope ─→ out_of_scope
/// </code>
/// The four terminal nodes yield the final <see cref="AgentRunState"/>. The framework's types stay in this namespace.
/// </summary>
internal sealed class AgenticRagWorkflow
{
    private readonly Workflow _workflow;

    public AgenticRagWorkflow(
        GuardrailExecutor guardrail,
        RetrieveExecutor retrieve,
        GradeExecutor grade,
        RewriteExecutor rewrite,
        GenerateExecutor generate,
        OutOfScopeExecutor outOfScope,
        MaxAttemptsExecutor maxAttempts,
        SearchUnavailableExecutor searchUnavailable,
        IOptions<AgenticRagOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var threshold = options.Value.GuardrailThreshold;
        var attempts = options.Value.MaxRetrievalAttempts;

        _workflow = new WorkflowBuilder(guardrail)
            .WithName("agentic_rag")
            .AddEdge<AgentRunState>(guardrail, retrieve, state => AgentRouting.InScope(state!, threshold))
            .AddEdge<AgentRunState>(guardrail, outOfScope, state => !AgentRouting.InScope(state!, threshold))
            .AddEdge<AgentRunState>(retrieve, grade, state => state!.SearchError is null)
            .AddEdge<AgentRunState>(retrieve, searchUnavailable, state => state!.SearchError is not null)
            .AddEdge<AgentRunState>(grade, generate, state => AgentRouting.AfterGrading(state!, attempts) == GradingRoute.GenerateAnswer)
            .AddEdge<AgentRunState>(grade, rewrite, state => AgentRouting.AfterGrading(state!, attempts) == GradingRoute.RewriteQuery)
            .AddEdge<AgentRunState>(grade, maxAttempts, state => AgentRouting.AfterGrading(state!, attempts) == GradingRoute.MaxAttempts)
            .AddEdge(rewrite, retrieve)
            .WithOutputFrom(generate, outOfScope, maxAttempts, searchUnavailable)
            .Build();
    }

    /// <summary>Runs the graph in-process and returns the state its terminal node yielded.</summary>
    /// <exception cref="OperationCanceledException">The request was cancelled.</exception>
    /// <remarks>An exception a node didn't handle is rethrown as is.</remarks>
    public async Task<AgentRunState> RunAsync(AgentRunState input, CancellationToken cancellationToken)
    {
        // The Concurrent environment lets one Workflow serve parallel runs. A handler's exception or a cancellation
        // doesn't make RunAsync throw: failures arrive as events, and a cancelled run returns without output.
        await using var run = await InProcessExecution.Concurrent.RunAsync(_workflow, input, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        AgentRunState? output = null;
        foreach (var workflowEvent in run.OutgoingEvents)
        {
            switch (workflowEvent)
            {
                case ExecutorFailedEvent { Data: { } exception }:
                    ExceptionDispatchInfo.Throw(exception);
                    break;
                case WorkflowErrorEvent { Exception: { } exception }:
                    throw new InvalidOperationException("The agentic workflow failed.", exception);
                case WorkflowOutputEvent result when result.Is<AgentRunState>(out var state):
                    output = state;
                    break;
            }
        }

        return output ?? throw new InvalidOperationException("The agentic workflow ended without an answer.");
    }
}

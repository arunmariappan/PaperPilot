using Microsoft.Agents.AI.Workflows;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// A workflow node: takes the run state and returns the updated state, which goes along the node's outgoing edges.
/// Nodes are stateless (services come from DI, per-request values from the state), so one workflow serves concurrent
/// runs.
/// </summary>
internal abstract class AgentNode(string id) : Executor<AgentRunState, AgentRunState>(id, declareCrossRunShareable: true);

/// <summary>A terminal node: completes the state and yields it as the workflow's output.</summary>
[YieldsOutput(typeof(AgentRunState))]
internal abstract class AgentEndNode(string id) : Executor<AgentRunState>(id, declareCrossRunShareable: true)
{
    public sealed override async ValueTask HandleAsync(
        AgentRunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await context.YieldOutputAsync(await CompleteAsync(message, cancellationToken), cancellationToken);
    }

    protected abstract ValueTask<AgentRunState> CompleteAsync(AgentRunState state, CancellationToken cancellationToken);
}

/// <summary>Where the run goes after grading.</summary>
internal enum GradingRoute
{
    GenerateAnswer,
    RewriteQuery,
    MaxAttempts,
}

/// <summary>The workflow's routing decisions, shared by the edges and the spans that report them.</summary>
internal static class AgentRouting
{
    /// <summary>In scope when the guardrail score reaches the threshold.</summary>
    public static bool InScope(AgentRunState state, int threshold) => state.Guardrail is null || state.Guardrail.Score >= threshold;

    /// <summary>
    /// Relevant documents → answer. Otherwise rewrite and retry, or stop once every attempt is used, without the
    /// rewrite Python made first (B5).
    /// </summary>
    public static GradingRoute AfterGrading(AgentRunState state, int maxAttempts) =>
        state.DocumentsRelevant ? GradingRoute.GenerateAnswer
        : state.RetrievalAttempts < maxAttempts ? GradingRoute.RewriteQuery
        : GradingRoute.MaxAttempts;

    /// <summary>The route as Python's <c>routing_decision</c> named it.</summary>
    public static string Name(GradingRoute route) => route switch
    {
        GradingRoute.GenerateAnswer => "generate_answer",
        GradingRoute.RewriteQuery => "rewrite_query",
        _ => "max_attempts",
    };
}

/// <summary>Fixed answers, word for word from Python where Python had them.</summary>
internal static class AgentMessages
{
    /// <summary>The generation prompt's context when nothing was retrieved.</summary>
    public const string NoDocuments = "No relevant documents found.";

    /// <summary>From <c>out_of_scope_node.py</c>.</summary>
    public static string OutOfScope(string question) =>
        "I apologize, but I can only help with questions about academic research papers "
        + "in Computer Science, Artificial Intelligence, and Machine Learning from arXiv.\n\n"
        + $"Your question: '{question}'\n\n"
        + "This appears to be outside my domain of expertise. For questions like this, you might want to try:\n"
        + "- General-purpose AI assistants for broad knowledge questions\n"
        + "- Domain-specific resources for topics outside CS/AI/ML\n"
        + "- Technical documentation if asking about specific software/tools\n\n"
        + "If you have a question about AI/ML research papers, I'd be happy to help!";

    /// <summary>From <c>retrieve_node.py</c>.</summary>
    public static string MaxAttempts(int attempts) =>
        $"I apologize, but I couldn't find relevant research papers after {attempts} attempts.\n"
        + "This may be because:\n"
        + "1. No papers in the database contain relevant information\n"
        + "2. The query terms don't match the indexed content\n\n"
        + "Please try rephrasing your question with more specific technical terms.";

    /// <summary>From <c>generate_answer_node.py</c>.</summary>
    public static string GenerationFailed(string error) =>
        $"I apologize, but I encountered an error while generating the answer: {error}\n\n"
        + "Please try again or rephrase your question.";

    /// <summary>New (B6): Python treated a search outage as "no papers found".</summary>
    public const string SearchUnavailable =
        "I apologize, but the paper search is unavailable right now, so I couldn't look for relevant research papers.\n\n"
        + "Please try again in a few minutes.";
}

using PaperPilot.Rag.Retrieval;

namespace PaperPilot.Rag.Agentic;

/// <summary>How an agentic run ended. Each ending has its own terminal executor.</summary>
public enum AgentEnding
{
    Answered,
    OutOfScope,
    MaxAttempts,
    SearchUnavailable,
}

/// <summary>
/// What the workflow passes from node to node. Each executor returns an updated copy (<c>with</c>), so concurrent runs
/// share nothing. Replaces Python's <c>AgentState</c> message list.
/// </summary>
internal sealed record AgentRunState
{
    /// <summary>The user's question. Grading and answer generation always use it (B27).</summary>
    public required string OriginalQuestion { get; init; }

    /// <summary>The query retrieval uses: the original question, or the latest rewrite.</summary>
    public required string CurrentQuery { get; init; }

    public required string Model { get; init; }

    public required int TopK { get; init; }

    public required bool UseHybrid { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    public GuardrailScoring? Guardrail { get; init; }

    public int RetrievalAttempts { get; init; }

    public RetrievalResult? LastRetrieval { get; init; }

    /// <summary>Set when the last retrieval failed because search was unavailable (B6).</summary>
    public string? SearchError { get; init; }

    /// <summary>One result per graded retrieval. A retrieval that found nothing isn't graded.</summary>
    public IReadOnlyList<GradingResult> Gradings { get; init; } = [];

    /// <summary>Whether the latest retrieval was graded relevant.</summary>
    public bool DocumentsRelevant { get; init; }

    public string? RewrittenQuery { get; init; }

    public string? Answer { get; init; }

    public AgentEnding? Ending { get; init; }
}

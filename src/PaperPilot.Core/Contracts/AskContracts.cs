using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Contracts;

/// <summary>Request body for <c>/ask</c>, <c>/stream</c> and <c>/ask-agentic</c>.</summary>
public sealed record AskRequest
{
    /// <summary>The question. <see cref="RequiredAttribute"/> also rejects whitespace-only questions (C13).</summary>
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Query { get; init; } = string.Empty;

    /// <summary>Number of chunks to retrieve.</summary>
    [Range(1, 10)]
    public int TopK { get; init; } = 3;

    /// <summary>BM25 + vector search; falls back to BM25 when the query can't be embedded.</summary>
    public bool UseHybrid { get; init; } = true;

    /// <summary>Ollama model to answer with. Null means <c>Ollama:Model</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Only use chunks from papers in these arXiv categories.</summary>
    public IReadOnlyList<string>? Categories { get; init; }
}

/// <summary>Response from <c>/ask</c>.</summary>
public record AskResponse
{
    public required string Query { get; init; }

    public required string Answer { get; init; }

    /// <summary>PDF URLs of the papers the answer drew on, without duplicates.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    public required int ChunksUsed { get; init; }

    /// <summary>The search mode actually used: <c>hybrid</c> or <c>bm25</c>.</summary>
    public required string SearchMode { get; init; }
}

/// <summary>Response from <c>/ask-agentic</c>.</summary>
public sealed record AgenticAskResponse : AskResponse
{
    public required IReadOnlyList<string> ReasoningSteps { get; init; }

    public required int RetrievalAttempts { get; init; }

    /// <summary>W3C trace id (32 hex characters), usable with <c>/feedback</c>.</summary>
    public string? TraceId { get; init; }
}

/// <summary>Request body for <c>/feedback</c>.</summary>
public sealed record FeedbackRequest
{
    [Required]
    public string TraceId { get; init; } = string.Empty;

    /// <summary>−1 (bad) to 1 (good).</summary>
    [Required, Range(-1.0, 1.0)]
    public double? Score { get; init; }

    [StringLength(1000)]
    public string? Comment { get; init; }
}

/// <summary>Response from <c>/feedback</c>.</summary>
public sealed record FeedbackResponse(bool Success, string Message);

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PaperPilot.Core.Contracts;

/// <summary>Request body for <c>/hybrid-search/</c>.</summary>
public sealed record HybridSearchRequest
{
    /// <summary>
    /// Search text. Unlike <see cref="AskRequest.Query"/>, whitespace is allowed: a blank query lists the latest papers.
    /// </summary>
    [Required(AllowEmptyStrings = true), StringLength(500, MinimumLength = 1)]
    public string Query { get; init; } = string.Empty;

    [Range(1, 100)]
    public int Size { get; init; } = 10;

    /// <summary>Offset for pagination. Ignored by hybrid search.</summary>
    [JsonPropertyName("from"), Range(0, int.MaxValue)]
    public int From { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>Sort by publication date instead of relevance. Ignored by hybrid search.</summary>
    public bool LatestPapers { get; init; }

    public bool UseHybrid { get; init; } = true;

    /// <summary>Hits scoring below this are dropped.</summary>
    [Range(0.0, double.MaxValue)]
    public double MinScore { get; init; }
}

/// <summary>One matching chunk.</summary>
public sealed record SearchHit
{
    public required string ArxivId { get; init; }

    public required string Title { get; init; }

    /// <summary>Comma-separated, as stored in the index.</summary>
    public string? Authors { get; init; }

    public string? Abstract { get; init; }

    public string? PublishedDate { get; init; }

    public string? PdfUrl { get; init; }

    public required double Score { get; init; }

    /// <summary>Highlighted fragments per field, with matches wrapped in <c>&lt;mark&gt;</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Highlights { get; init; }

    public string? ChunkText { get; init; }

    public string? ChunkId { get; init; }

    public string? SectionName { get; init; }
}

/// <summary>Response from <c>/hybrid-search/</c>.</summary>
public sealed record SearchResponse
{
    public required string Query { get; init; }

    public required int Total { get; init; }

    public required IReadOnlyList<SearchHit> Hits { get; init; }

    public required int Size { get; init; }

    [JsonPropertyName("from")]
    public required int From { get; init; }

    /// <summary>The search mode actually used: <c>hybrid</c> or <c>bm25</c>.</summary>
    public string? SearchMode { get; init; }

    public string? Error { get; init; }
}

namespace PaperPilot.Core.Search;

/// <summary>A chunk search. BM25 uses every field; hybrid search ignores <c>From</c> and <c>LatestPapers</c>.</summary>
/// <param name="Query">Search text. Blank means "all chunks", sorted by publication date.</param>
/// <param name="MinScore">Hybrid only: hits scoring below this are dropped.</param>
public sealed record SearchQuery(
    string Query,
    int Size = 10,
    int From = 0,
    IReadOnlyList<string>? Categories = null,
    bool LatestPapers = false,
    double MinScore = 0);

/// <summary>The search modes, as reported in <c>search_mode</c>.</summary>
public static class SearchModes
{
    public const string Hybrid = "hybrid";
    public const string Bm25 = "bm25";
}

/// <summary>One chunk from the index.</summary>
/// <param name="ChunkId">The document <c>_id</c>.</param>
/// <param name="Authors">Comma-separated, as stored.</param>
/// <param name="PublishedDate">As stored in <c>_source</c>, unparsed.</param>
public sealed record ChunkHit(
    string ChunkId,
    string ArxivId,
    string Title,
    string? Authors,
    string? Abstract,
    string? PublishedDate,
    string? ChunkText,
    int? ChunkIndex,
    string? SectionTitle,
    double Score,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Highlights);

/// <summary>Search results.</summary>
/// <param name="Total">Total matches for BM25; for hybrid, the number of hits after <c>MinScore</c> filtering.</param>
/// <param name="Mode">The mode actually used (<see cref="SearchModes"/>).</param>
public sealed record SearchResult(long Total, IReadOnlyList<ChunkHit> Hits, string Mode);

/// <summary>Index statistics for the chunk index.</summary>
public sealed record IndexStats(string IndexName, bool Exists, long DocumentCount, long DeletedCount, long SizeInBytes);

/// <summary>Outcome of a bulk index request, counted from the per-item results.</summary>
public sealed record BulkIndexResult(int Succeeded, int Failed, IReadOnlyList<string> Errors);

/// <summary>A chunk document to index. The document id is <c>{ArxivId}:{ChunkIndex}</c> (C5).</summary>
public sealed record ChunkDocument
{
    public required string ArxivId { get; init; }

    public required string PaperId { get; init; }

    public required int ChunkIndex { get; init; }

    public required string ChunkText { get; init; }

    public required int ChunkWordCount { get; init; }

    public required int StartChar { get; init; }

    public required int EndChar { get; init; }

    public string? SectionTitle { get; init; }

    public required string EmbeddingModel { get; init; }

    public required string Title { get; init; }

    /// <summary>Comma-separated author names.</summary>
    public required string Authors { get; init; }

    public required string Abstract { get; init; }

    public required IReadOnlyList<string> Categories { get; init; }

    public required DateTimeOffset PublishedDate { get; init; }

    public required IReadOnlyList<float> Embedding { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    public string ChunkId => DocumentId(ArxivId, ChunkIndex);

    /// <summary>The deterministic document id that makes re-indexing idempotent.</summary>
    public static string DocumentId(string arxivId, int chunkIndex) => $"{arxivId}:{chunkIndex}";
}

/// <summary>Turns text into vectors for hybrid search.</summary>
public interface IEmbeddingService
{
    /// <summary>Embeds a search query (Jina task <c>retrieval.query</c>). Retries briefly on 429.</summary>
    /// <exception cref="Exceptions.EmbeddingUnavailableException">No key, or Jina failed.</exception>
    Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Embeds chunks for indexing (Jina task <c>retrieval.passage</c>), in batches, in input order.</summary>
    /// <exception cref="Exceptions.EmbeddingUnavailableException">No key, or Jina failed.</exception>
    Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
        IReadOnlyList<string> passages, int batchSize = 50, CancellationToken cancellationToken = default);
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Search;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Retrieval;

/// <summary>Chunks retrieved for a question.</summary>
/// <param name="Sources">De-duplicated PDF URLs of the chunks' papers, in first-seen order.</param>
/// <param name="ArxivIds">The chunks' arXiv ids, one per chunk (with repeats).</param>
/// <param name="TotalHits">Total matches as reported by the search (for hybrid search, the hit count).</param>
/// <param name="SearchMode">The mode actually used: <c>bm25</c> when hybrid was requested but embedding failed (B24).</param>
public sealed record RetrievalResult(
    IReadOnlyList<ChunkHit> Chunks,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> ArxivIds,
    long TotalHits,
    string SearchMode);

/// <summary>Retrieval shared by classic RAG, the agent (phase 5) and Telegram (phase 6).</summary>
public interface IPaperRetriever
{
    /// <summary>
    /// Embeds the query (only for hybrid search) and searches for the <paramref name="topK"/> best chunks.
    /// If embedding fails, it logs a warning and uses BM25.
    /// </summary>
    /// <exception cref="SearchUnavailableException">OpenSearch can't be reached (B6).</exception>
    /// <exception cref="SearchQueryException">OpenSearch rejected the query.</exception>
    Task<RetrievalResult> RetrieveAsync(
        string query, int topK, bool useHybrid, IReadOnlyList<string>? categories, CancellationToken cancellationToken = default);
}

internal sealed partial class PaperRetriever(
    OpenSearchClient search, IEmbeddingService embeddings, TimeProvider time, ILogger<PaperRetriever> logger) : IPaperRetriever
{
    public async Task<RetrievalResult> RetrieveAsync(
        string query, int topK, bool useHybrid, IReadOnlyList<string>? categories, CancellationToken cancellationToken = default)
    {
        var embedding = useHybrid ? await EmbedAsync(query, cancellationToken) : null;

        using var span = RagTelemetry.Source.StartActivity("search_retrieval");
        span.SetInput(new { Query = query, TopK = topK });

        SearchResult result;
        try
        {
            result = await search.SearchAsync(new SearchQuery(query, topK, Categories: categories), embedding, cancellationToken);
        }
        catch (Exception ex) when (ex is SearchUnavailableException or SearchQueryException)
        {
            span.Fail(ex);
            throw;
        }

        var arxivIds = result.Hits.Select(h => h.ArxivId).Where(id => !string.IsNullOrEmpty(id)).ToList();
        var uniqueIds = arxivIds.Distinct().ToList();
        span?.SetTag("search_mode", result.Mode);
        span.SetOutput(new
        {
            ChunksReturned = result.Hits.Count,
            UniquePapers = uniqueIds.Count,
            TotalHits = result.Total,
            ArxivIds = uniqueIds,
        });

        return new RetrievalResult(
            result.Hits, [.. uniqueIds.Select(ArxivId.ToPdfUrl).Distinct()], arxivIds, result.Total, result.Mode);
    }

    private async Task<float[]?> EmbedAsync(string query, CancellationToken cancellationToken)
    {
        using var span = RagTelemetry.Source.StartActivity("query_embedding");
        span.SetInput(new { Query = query, QueryLength = query.Length });
        var started = time.GetTimestamp();

        try
        {
            var embedding = await embeddings.EmbedQueryAsync(query, cancellationToken);
            span.SetOutput(new { EmbeddingDurationMs = Math.Round(time.GetElapsedTime(started).TotalMilliseconds, 2), Success = true });
            return embedding;
        }
        catch (EmbeddingUnavailableException ex)
        {
            LogEmbeddingFailed(logger, ex.Message);
            span.Fail(ex);
            span.SetOutput(new { Success = false, Error = ex.Message });
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to generate embeddings, falling back to BM25: {Reason}")]
    private static partial void LogEmbeddingFailed(ILogger logger, string reason);
}

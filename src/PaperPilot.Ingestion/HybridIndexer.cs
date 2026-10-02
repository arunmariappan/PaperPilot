using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Indexing;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;

namespace PaperPilot.Ingestion;

/// <summary>What the index step did.</summary>
/// <param name="PapersWithContent">Papers that produced at least one chunk.</param>
public sealed record IndexResult(
    int PapersProcessed,
    int PapersWithContent,
    int ChunksCreated,
    int ChunksIndexed,
    int EmbeddingsGenerated,
    IReadOnlyList<string> Errors);

/// <summary>
/// Chunks, embeds and indexes papers into the hybrid search index, as Python's <c>HybridIndexingService</c> did.
/// A paper's old chunks are replaced only once its new embeddings are ready, so a Jina outage leaves the index as it
/// was instead of emptying the paper.
/// </summary>
public sealed partial class HybridIndexer(
    PaperRepository papers,
    OpenSearchClient search,
    IEmbeddingService embeddings,
    TextChunker chunker,
    IOptions<JinaOptions> jina,
    TimeProvider time,
    ILogger<HybridIndexer> logger)
{
    private const int EmbeddingBatchSize = 50;

    /// <summary>
    /// Indexes exactly the given papers (B13). A paper without content gets no chunks, which isn't an error.
    /// A failure for one paper is recorded in <see cref="IndexResult.Errors"/> and never stops the others.
    /// </summary>
    public async Task<IndexResult> IndexPapersAsync(
        IReadOnlyCollection<Guid> paperIds, bool replaceExisting = true, CancellationToken cancellationToken = default)
    {
        var toIndex = await papers.GetByIdsAsync(paperIds, cancellationToken);
        int withContent = 0, created = 0, indexed = 0, embedded = 0;
        var errors = new List<string>();

        foreach (var paper in toIndex)
        {
            try
            {
                var chunks = chunker.ChunkPaper(paper.Title, paper.Abstract, paper.RawText, paper.Sections);
                if (chunks.Count == 0)
                {
                    if (replaceExisting)
                    {
                        await search.DeletePaperChunksAsync(paper.ArxivId, cancellationToken);
                    }

                    LogNoChunks(logger, paper.ArxivId);
                    continue;
                }

                withContent++;
                created += chunks.Count;
                var vectors = await embeddings.EmbedPassagesAsync([.. chunks.Select(c => c.Text)], EmbeddingBatchSize, cancellationToken);
                embedded += vectors.Count;
                if (vectors.Count != chunks.Count)
                {
                    errors.Add($"Embedding count mismatch for {paper.ArxivId}: {vectors.Count} != {chunks.Count}");
                    continue;
                }

                if (replaceExisting)
                {
                    await search.DeletePaperChunksAsync(paper.ArxivId, cancellationToken);
                }

                var now = time.GetUtcNow();
                var bulk = await search.BulkIndexChunksAsync(chunks.Zip(vectors, (chunk, vector) => new ChunkDocument
                {
                    ArxivId = paper.ArxivId,
                    PaperId = paper.Id.ToString(),
                    ChunkIndex = chunk.ChunkIndex,
                    ChunkText = chunk.Text,
                    ChunkWordCount = chunk.WordCount,
                    StartChar = chunk.StartChar,
                    EndChar = chunk.EndChar,
                    SectionTitle = chunk.SectionTitle,
                    EmbeddingModel = jina.Value.Model,
                    Title = paper.Title,
                    Authors = string.Join(", ", paper.Authors),
                    Abstract = paper.Abstract,
                    Categories = paper.Categories,
                    PublishedDate = paper.PublishedDate,
                    Embedding = vector,
                    CreatedAt = now,
                    UpdatedAt = now,
                }), cancellationToken);

                indexed += bulk.Succeeded;
                errors.AddRange(bulk.Errors.Select(e => $"Failed to index a chunk of {paper.ArxivId}: {e}"));
                LogIndexed(logger, paper.ArxivId, bulk.Succeeded, bulk.Failed);
            }
            catch (Exception ex) when (ex is EmbeddingUnavailableException or SearchUnavailableException or SearchQueryException)
            {
                errors.Add($"Error indexing paper {paper.ArxivId}: {ex.Message}");
            }
        }

        LogDone(logger, toIndex.Count, indexed);
        return new IndexResult(toIndex.Count, withContent, created, indexed, embedded, errors);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No chunks created for paper {ArxivId}")]
    private static partial void LogNoChunks(ILogger logger, string arxivId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Indexed paper {ArxivId}: {Succeeded} chunks successful, {Failed} failed")]
    private static partial void LogIndexed(ILogger logger, string arxivId, int succeeded, int failed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Batch indexing complete: {Papers} papers, {Chunks} chunks indexed")]
    private static partial void LogDone(ILogger logger, int papers, int chunks);
}

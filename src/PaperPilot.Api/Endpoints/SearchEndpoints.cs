using System.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Search;

namespace PaperPilot.Api.Endpoints;

internal static partial class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder group)
    {
        // Matches /hybrid-search and /hybrid-search/ alike.
        group.MapPost("/hybrid-search/", SearchAsync)
            .WithName("HybridSearch")
            .WithTags("hybrid-search")
            .WithSummary("Search paper chunks with BM25 or hybrid (BM25 + vector) search")
            .WithDescription(
                "Hybrid search runs when `use_hybrid` is true and the query can be embedded; otherwise BM25 runs and "
                + "`search_mode` says so. Hybrid search ignores `from` and `latest_papers`, and `min_score` applies "
                + "to hybrid search only. A blank query lists the newest chunks.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<Results<Ok<SearchResponse>, ProblemHttpResult>> SearchAsync(
        HybridSearchRequest request,
        OpenSearchClient search,
        IEmbeddingService embeddings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(SearchEndpoints));

        if (!await search.HealthAsync(cancellationToken))
        {
            return TypedResults.Problem(
                "Search service is currently unavailable", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        float[]? embedding = null;
        if (request.UseHybrid)
        {
            try
            {
                embedding = await embeddings.EmbedQueryAsync(request.Query, cancellationToken);
            }
            catch (EmbeddingUnavailableException ex)
            {
                LogEmbeddingFailed(logger, ex.Message);
                Activity.Current?.AddEvent(new ActivityEvent("embedding_failed_bm25_fallback"));
            }
        }

        try
        {
            var query = new SearchQuery(
                request.Query, request.Size, request.From, request.Categories, request.LatestPapers, request.MinScore);
            var result = await search.SearchAsync(query, embedding, cancellationToken);

            return TypedResults.Ok(new SearchResponse
            {
                Query = request.Query,
                Total = (int)Math.Min(result.Total, int.MaxValue),
                Hits = [.. result.Hits.Select(SearchHitMapper.ToSearchHit)],
                Size = request.Size,
                From = request.From,
                SearchMode = result.Mode,
            });
        }
        catch (SearchUnavailableException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SearchQueryException ex)
        {
            return TypedResults.Problem($"Search failed: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to generate embeddings, falling back to BM25: {Reason}")]
    private static partial void LogEmbeddingFailed(ILogger logger, string reason);
}

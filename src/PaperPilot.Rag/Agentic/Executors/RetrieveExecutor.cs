using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Rag.Retrieval;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// Counts the attempt and retrieves chunks for the current query (the original question, or the latest rewrite).
/// Retrieval falls back to BM25 when the query can't be embedded (B3). A search outage is recorded in the state and
/// ends the run with an explicit answer (B6); other search errors fail the request.
/// </summary>
internal sealed partial class RetrieveExecutor(
    IPaperRetriever retriever, IOptions<AgenticRagOptions> options, ILogger<RetrieveExecutor> logger)
    : AgentNode("retrieve")
{
    public override async ValueTask<AgentRunState> HandleAsync(
        AgentRunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var attempt = message.RetrievalAttempts + 1;
        var state = message with { RetrievalAttempts = attempt };

        using var span = RagTelemetry.Source.StartActivity("document_retrieval_initiation");
        span.SetInput(new { Query = state.CurrentQuery, Attempt = attempt, MaxAttempts = options.Value.MaxRetrievalAttempts });
        span.SetMetadata(("node", "retrieve"), ("top_k", state.TopK));

        try
        {
            var retrieval = await retriever.RetrieveAsync(
                state.CurrentQuery, state.TopK, state.UseHybrid, state.Categories, cancellationToken);
            span.SetOutput(new
            {
                Status = "retrieved",
                Query = state.CurrentQuery,
                Attempt = attempt,
                ChunksRetrieved = retrieval.Chunks.Count,
                retrieval.SearchMode,
            });
            return state with { LastRetrieval = retrieval, SearchError = null };
        }
        catch (SearchUnavailableException ex)
        {
            LogSearchUnavailable(logger, ex);
            span.Degrade(ex, ObservationLevels.Error);
            span.SetOutput(new { Status = "search_unavailable", Error = ex.Message });
            return state with { SearchError = ex.Message };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            span.Fail(ex);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Search is unavailable, ending the agentic run")]
    private static partial void LogSearchUnavailable(ILogger logger, Exception exception);
}

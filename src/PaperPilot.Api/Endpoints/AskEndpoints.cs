using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Rag;

namespace PaperPilot.Api.Endpoints;

internal static partial class AskEndpoints
{
    public static RouteGroupBuilder MapAskEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/ask", AskAsync)
            .WithName("Ask")
            .WithTags("ask")
            .WithSummary("Answer a question from the indexed papers")
            .WithDescription(
                "Retrieves the `top_k` best chunks (hybrid search, or BM25 when `use_hybrid` is false or the query can't "
                + "be embedded) and has the Ollama model answer from them. Answers are cached by exact request for "
                + "`Cache:TtlHours`. `search_mode` reports the mode actually used.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/stream", Stream)
            .WithName("Stream")
            .WithTags("stream")
            .WithSummary("Answer a question, streaming the answer as server-sent events")
            .WithDescription(
                "Each event's `data` is a JSON object: first `{sources, chunks_used, search_mode}`, then `{chunk}` for each "
                + "piece of the answer, then `{answer, done: true}`. When no chunks match, the only event is "
                + "`{answer, sources: [], done: true}`; a failure ends the stream with `{error}`.")
            .Produces<RagStreamEvent>(contentType: "text/event-stream")
            .ProducesValidationProblem();

        return group;
    }

    private static async Task<Results<Ok<AskResponse>, ProblemHttpResult>> AskAsync(
        AskRequest request, RagService rag, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await rag.AskAsync(request, cancellationToken));
        }
        catch (SearchUnavailableException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (SearchQueryException ex)
        {
            return TypedResults.Problem($"Search failed: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Like Python's HTTPException(500, str(e)): the client sees what went wrong, e.g. Ollama being down.
            LogAskFailed(loggerFactory.CreateLogger(typeof(AskEndpoints)), ex);
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static ServerSentEventsResult<RagStreamEvent> Stream(
        AskRequest request, RagService rag, CancellationToken cancellationToken) =>
        TypedResults.ServerSentEvents(rag.StreamAsync(request, cancellationToken));

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing request")]
    private static partial void LogAskFailed(ILogger logger, Exception exception);
}

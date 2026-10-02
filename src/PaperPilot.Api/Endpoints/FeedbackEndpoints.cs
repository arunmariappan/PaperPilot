using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Contracts;
using PaperPilot.Infrastructure.Observability;

namespace PaperPilot.Api.Endpoints;

internal static class FeedbackEndpoints
{
    public static RouteGroupBuilder MapFeedbackEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/feedback", SubmitAsync)
            .WithName("Feedback")
            .WithTags("feedback")
            .WithSummary("Rate an answer")
            .WithDescription(
                "Attaches a `user-feedback` score (−1..1) and an optional comment to the Langfuse trace `trace_id`. "
                + "Returns 503 when Langfuse is disabled.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<Results<Ok<FeedbackResponse>, ProblemHttpResult>> SubmitAsync(
        FeedbackRequest request, LangfuseScoresClient langfuse, CancellationToken cancellationToken)
    {
        // Python returned 500 here because its tracer object always existed (B16).
        if (!langfuse.IsEnabled)
        {
            return TypedResults.Problem(
                "Langfuse tracing is disabled. Cannot submit feedback.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            await langfuse.SubmitAsync(request.TraceId, request.Score!.Value, request.Comment, cancellationToken);
            return TypedResults.Ok(new FeedbackResponse(true, "Feedback recorded successfully"));
        }
        catch (LangfuseException ex)
        {
            return TypedResults.Problem(
                $"Failed to submit feedback to Langfuse: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

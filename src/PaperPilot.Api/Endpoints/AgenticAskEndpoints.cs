using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Contracts;
using PaperPilot.Rag.Agentic;

namespace PaperPilot.Api.Endpoints;

internal static partial class AgenticAskEndpoints
{
    public static RouteGroupBuilder MapAgenticAskEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/ask-agentic", AskAgenticAsync)
            .WithName("AskAgentic")
            .WithTags("agentic-rag")
            .WithSummary("Answer a question with the agentic RAG workflow")
            .WithDescription(
                "Scores whether the question is about CS/AI/ML research and declines it if not. Otherwise it retrieves "
                + "`top_k` chunks, has the model grade them, and rewrites the query and retries when they aren't relevant "
                + "(up to `Agentic:MaxRetrievalAttempts` retrievals) before answering. `reasoning_steps` lists what it did, "
                + "and `trace_id` can be sent to `/feedback`. Answers aren't cached. LLM failures fall back to defaults and "
                + "a search outage gives an explicit answer, so a 500 means something unexpected went wrong.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return group;
    }

    private static async Task<Results<Ok<AgenticAskResponse>, ProblemHttpResult>> AskAgenticAsync(
        AskRequest request, IAgenticRagService agent, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await agent.AskAsync(request, cancellationToken));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogAskFailed(loggerFactory.CreateLogger(typeof(AgenticAskEndpoints)), ex);
            return TypedResults.Problem(
                $"Error processing question: {ex.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error processing agentic question")]
    private static partial void LogAskFailed(ILogger logger, Exception exception);
}

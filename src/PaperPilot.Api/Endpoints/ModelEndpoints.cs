using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Contracts;
using PaperPilot.Infrastructure.Llm;

namespace PaperPilot.Api.Endpoints;

internal static class ModelEndpoints
{
    public static RouteGroupBuilder MapModelEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/models", ListAsync)
            .WithName("Models")
            .WithTags("models")
            .WithSummary("List the installed Ollama models")
            .WithDescription("`default_model` is used when a request doesn't name a `model`.")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<Results<Ok<ModelsResponse>, ProblemHttpResult>> ListAsync(
        OllamaModelCatalog catalog, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(new ModelsResponse(catalog.DefaultModel, await catalog.ListModelsAsync(cancellationToken)));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return TypedResults.Problem(
                $"Cannot list Ollama models: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

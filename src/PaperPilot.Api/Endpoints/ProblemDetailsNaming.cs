using System.Text.Json;

namespace PaperPilot.Api.Endpoints;

internal static class ProblemDetailsNaming
{
    /// <summary>Validation errors are keyed by C# property names (<c>TopK</c>); report them as the wire names (<c>top_k</c>).</summary>
    public static void UseSnakeCaseErrorKeys(ProblemDetailsContext context)
    {
        if (context.ProblemDetails is not HttpValidationProblemDetails validation || validation.Errors.Count == 0)
        {
            return;
        }

        var errors = validation.Errors.ToList();
        validation.Errors.Clear();
        foreach (var (key, messages) in errors)
        {
            validation.Errors[JsonNamingPolicy.SnakeCaseLower.ConvertName(key)] = messages;
        }
    }
}

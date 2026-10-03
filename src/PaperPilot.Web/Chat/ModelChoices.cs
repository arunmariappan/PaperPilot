using PaperPilot.Core.Contracts;

namespace PaperPilot.Web.Chat;

/// <summary>The model dropdown: the API's default first, then the other installed models.</summary>
public sealed record ModelChoices(string DefaultModel, IReadOnlyList<string> Models)
{
    /// <summary>Before <c>/models</c> answers, or when it fails: one entry that leaves the choice to the API.</summary>
    public static ModelChoices ServerDefault { get; } = new(string.Empty, [string.Empty]);

    public static ModelChoices From(ModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(response.DefaultModel, [.. response.Models.Prepend(response.DefaultModel).Distinct(StringComparer.Ordinal)]);
    }

    public static string Label(string model) => model.Length == 0 ? "Server default" : model;
}

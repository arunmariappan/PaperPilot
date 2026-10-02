using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Llm;

/// <summary>Builds <see cref="ChatOptions"/> for Ollama calls from <c>Ollama:*</c> settings.</summary>
public sealed class ChatOptionsFactory(IOptions<OllamaOptions> options)
{
    /// <summary><c>Ollama:Model</c>, used when a request doesn't name a model.</summary>
    public string DefaultModel => options.Value.Model;

    /// <summary>The requested model, or <see cref="DefaultModel"/> when none (or a blank one) is given.</summary>
    public string ResolveModel(string? model) => string.IsNullOrWhiteSpace(model) ? DefaultModel : model;

    /// <summary>
    /// Options for one call. OllamaSharp sends <paramref name="temperature"/> and <paramref name="topP"/> in Ollama's
    /// <c>options</c> object and <c>think</c> (<c>Ollama:Think</c>) at the top level of the request.
    /// </summary>
    public ChatOptions Create(string? model, float temperature, float? topP = null) => new()
    {
        ModelId = ResolveModel(model),
        Temperature = temperature,
        TopP = topP,
        AdditionalProperties = new() { ["think"] = options.Value.Think },
    };
}

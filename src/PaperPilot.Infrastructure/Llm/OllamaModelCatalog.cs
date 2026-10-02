using OllamaSharp;

namespace PaperPilot.Infrastructure.Llm;

/// <summary>The models installed in Ollama, for <c>/models</c> (N1).</summary>
public sealed class OllamaModelCatalog(IOllamaApiClient ollama, ChatOptionsFactory chatOptions)
{
    public string DefaultModel => chatOptions.DefaultModel;

    /// <summary>Installed model names (<c>GET /api/tags</c>), sorted. Throws when Ollama can't be reached.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await ollama.ListLocalModelsAsync(cancellationToken);
        return [.. models.Select(m => m.Name).Order(StringComparer.Ordinal)];
    }
}

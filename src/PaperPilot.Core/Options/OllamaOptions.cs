using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>Generation settings. The endpoint comes from the <c>ollama</c> connection string.</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>Used when a request doesn't name a model.</summary>
    [Required]
    public string Model { get; set; } = "qwen3.5:9b";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Thinking models (e.g. qwen3) are very slow with thinking on.</summary>
    public bool Think { get; set; }
}

using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>Jina AI embeddings. Without an API key, search falls back to BM25 and ingestion can't embed chunks.</summary>
public sealed class JinaOptions
{
    public const string SectionName = "Jina";

    public string ApiKey { get; set; } = string.Empty;

    [Required, Url]
    public string BaseUrl { get; set; } = "https://api.jina.ai/v1/";

    [Required]
    public string Model { get; set; } = "jina-embeddings-v3";
}

using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>The OpenSearch cluster and the hybrid chunk index. The AppHost sets <c>Host</c>.</summary>
public sealed class OpenSearchOptions
{
    public const string SectionName = "OpenSearch";

    [Required, Url]
    public string Host { get; set; } = "http://localhost:9200";

    [Required]
    public string IndexName { get; set; } = "arxiv-papers";

    [Required]
    public string ChunkIndexSuffix { get; set; } = "chunks";

    /// <summary>The single hybrid index: <c>{IndexName}-{ChunkIndexSuffix}</c>.</summary>
    public string ChunkIndexName => $"{IndexName}-{ChunkIndexSuffix}";

    /// <summary>Must match the embedding model's output size (jina-embeddings-v3: 1024).</summary>
    [Range(1, 16_000)]
    public int VectorDimension { get; set; } = 1024;

    [Required, AllowedValues("cosinesimil", "l2", "innerproduct")]
    public string VectorSpaceType { get; set; } = "cosinesimil";

    [Required]
    public string RrfPipelineName { get; set; } = "hybrid-rrf-pipeline";

    /// <summary>Hybrid search asks each sub-query for <c>size × multiplier</c> hits before fusing them.</summary>
    [Range(1, 20)]
    public int HybridSearchSizeMultiplier { get; set; } = 2;
}

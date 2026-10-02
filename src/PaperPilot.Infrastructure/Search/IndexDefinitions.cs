using System.Text.Json.Nodes;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Search;

/// <summary>The chunk index mapping and the RRF search pipeline, from embedded JSON.</summary>
public static class IndexDefinitions
{
    /// <summary>
    /// The hybrid chunk index (<c>dynamic: strict</c>, BM25 analyzers, HNSW k-NN vector). The vector dimension and
    /// space type come from options; Python hard-coded 1024 and cosinesimil (B11).
    /// </summary>
    public static JsonObject ChunksIndex(OpenSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var index = Load("chunks-index.json");
        index["settings"]!["index.knn.space_type"] = options.VectorSpaceType;
        var embedding = index["mappings"]!["properties"]!["embedding"]!;
        embedding["dimension"] = options.VectorDimension;
        embedding["method"]!["space_type"] = options.VectorSpaceType;
        return index;
    }

    /// <summary>Body of <c>PUT /_search/pipeline/{id}</c>: reciprocal rank fusion with k = 60.</summary>
    public static JsonObject RrfPipeline() => Load("rrf-pipeline.json");

    private static JsonObject Load(string name)
    {
        using var stream = typeof(IndexDefinitions).Assembly.GetManifestResourceStream($"PaperPilot.Infrastructure.Search.{name}")
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");
        return JsonNode.Parse(stream)!.AsObject();
    }
}

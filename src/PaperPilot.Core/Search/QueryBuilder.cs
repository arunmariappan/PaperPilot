using System.Text.Json.Nodes;

namespace PaperPilot.Core.Search;

/// <summary>
/// Builds OpenSearch request bodies for chunk search. Output is golden-tested against the Python query builder.
/// </summary>
public static class QueryBuilder
{
    private static readonly string[] SearchFields = ["chunk_text^3", "title^2", "abstract^1"];

    /// <summary>A BM25 search: multi_match over chunk text, title and abstract, with highlighting.</summary>
    public static JsonObject BuildBm25(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var body = new JsonObject
        {
            ["query"] = BuildBoolQuery(query),
            ["size"] = query.Size,
            ["from"] = query.From,
            ["track_total_hits"] = true,
            ["_source"] = new JsonObject { ["excludes"] = new JsonArray("embedding") },
            ["highlight"] = BuildHighlight(),
        };

        // A blank query has no relevance to sort by, so it lists the newest papers.
        if (query.LatestPapers || string.IsNullOrWhiteSpace(query.Query))
        {
            body["sort"] = new JsonArray(
                new JsonObject { ["published_date"] = new JsonObject { ["order"] = "desc" } },
                "_score");
        }

        return body;
    }

    /// <summary>
    /// A hybrid search: the BM25 query and a k-NN query over <paramref name="embedding"/>, each asked for
    /// <c>Size × multiplier</c> hits, fused by the RRF search pipeline. Ignores <c>From</c> and <c>LatestPapers</c>.
    /// </summary>
    public static JsonObject BuildHybrid(SearchQuery query, IReadOnlyList<float> embedding, int multiplier)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(embedding);
        ArgumentOutOfRangeException.ThrowIfLessThan(multiplier, 1);

        var candidates = query.Size * multiplier;
        var bm25 = BuildBm25(query with { Size = candidates, From = 0, LatestPapers = false });

        var vector = new JsonArray();
        foreach (var value in embedding)
        {
            vector.Add(value);
        }

        return new JsonObject
        {
            ["size"] = query.Size,
            ["query"] = new JsonObject
            {
                ["hybrid"] = new JsonObject
                {
                    ["queries"] = new JsonArray(
                        bm25["query"]!.DeepClone(),
                        new JsonObject
                        {
                            ["knn"] = new JsonObject
                            {
                                ["embedding"] = new JsonObject { ["vector"] = vector, ["k"] = candidates },
                            },
                        }),
                },
            },
            ["_source"] = bm25["_source"]!.DeepClone(),
            ["highlight"] = bm25["highlight"]!.DeepClone(),
        };
    }

    private static JsonObject BuildBoolQuery(SearchQuery query)
    {
        var must = string.IsNullOrWhiteSpace(query.Query)
            ? new JsonArray(new JsonObject { ["match_all"] = new JsonObject() })
            : new JsonArray(new JsonObject
            {
                ["multi_match"] = new JsonObject
                {
                    ["query"] = query.Query,
                    ["fields"] = new JsonArray([.. SearchFields.Select(f => (JsonNode)f)]),
                    ["type"] = "best_fields",
                    ["operator"] = "or",
                    ["fuzziness"] = "AUTO",
                    ["prefix_length"] = 2,
                },
            });

        var boolQuery = new JsonObject { ["must"] = must };
        if (query.Categories is { Count: > 0 } categories)
        {
            boolQuery["filter"] = new JsonArray(new JsonObject
            {
                ["terms"] = new JsonObject { ["categories"] = new JsonArray([.. categories.Select(c => (JsonNode)c)]) },
            });
        }

        return new JsonObject { ["bool"] = boolQuery };
    }

    private static JsonObject BuildHighlight()
    {
        static JsonObject Field(int fragmentSize, int fragments) => new()
        {
            ["fragment_size"] = fragmentSize,
            ["number_of_fragments"] = fragments,
            ["pre_tags"] = new JsonArray("<mark>"),
            ["post_tags"] = new JsonArray("</mark>"),
        };

        return new JsonObject
        {
            ["fields"] = new JsonObject
            {
                ["chunk_text"] = Field(150, 2),
                ["title"] = Field(0, 0),
                ["abstract"] = Field(150, 1),
            },
            ["require_field_match"] = false,
        };
    }
}

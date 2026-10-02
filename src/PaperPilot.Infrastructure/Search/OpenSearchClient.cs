using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace PaperPilot.Infrastructure.Search;

/// <summary>
/// The chunk index over OpenSearch's REST API. Failures throw instead of returning empty results (B6):
/// <see cref="SearchUnavailableException"/> when the cluster can't be reached, <see cref="SearchQueryException"/>
/// when it rejects a request.
/// </summary>
public sealed partial class OpenSearchClient(HttpClient http, IOptions<OpenSearchOptions> options, ILogger<OpenSearchClient> logger)
{
    private readonly OpenSearchOptions _options = options.Value;

    public string IndexName => _options.ChunkIndexName;

    /// <summary>True when the cluster responds with status green or yellow. Never throws.</summary>
    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var health = await SendAsync(HttpMethod.Get, "_cluster/health", null, cancellationToken);
            return health?["status"]?.GetValue<string>() is "green" or "yellow";
        }
        catch (Exception ex) when (ex is SearchUnavailableException or SearchQueryException)
        {
            LogHealthCheckFailed(ex.Message);
            return false;
        }
    }

    public async Task<IndexStats> GetIndexStatsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IndexExistsAsync(cancellationToken))
        {
            return new IndexStats(IndexName, Exists: false, 0, 0, 0);
        }

        var stats = await SendAsync(HttpMethod.Get, $"{IndexName}/_stats", null, cancellationToken);
        var total = stats!["indices"]![IndexName]!["total"]!;
        return new IndexStats(
            IndexName,
            Exists: true,
            DocumentCount: total["docs"]!["count"]!.GetValue<long>(),
            DeletedCount: total["docs"]!["deleted"]!.GetValue<long>(),
            SizeInBytes: total["store"]!["size_in_bytes"]!.GetValue<long>());
    }

    /// <summary>Creates the chunk index if it's missing (or recreates it when forced). Returns true if created.</summary>
    public async Task<bool> EnsureIndexAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force && await IndexExistsAsync(cancellationToken))
        {
            await SendAsync(HttpMethod.Delete, IndexName, null, cancellationToken);
            LogIndexDeleted(IndexName);
        }

        if (await IndexExistsAsync(cancellationToken))
        {
            return false;
        }

        try
        {
            await SendAsync(HttpMethod.Put, IndexName, IndexDefinitions.ChunksIndex(_options), cancellationToken);
            LogIndexCreated(IndexName);
            return true;
        }
        catch (SearchQueryException ex) when (ex.Reason?.Contains("already exists", StringComparison.Ordinal) == true
            || ex.Message.Contains("resource_already_exists_exception", StringComparison.Ordinal))
        {
            // Api and Worker both run this at startup; the loser of the race sees the index already there.
            return false;
        }
    }

    /// <summary>
    /// Creates the RRF search pipeline if it's missing (or recreates it when forced). Returns true if created.
    /// Checks the search pipeline API; Python checked the ingest API and re-created it on every start (B12).
    /// </summary>
    public async Task<bool> EnsureRrfPipelineAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var path = $"_search/pipeline/{_options.RrfPipelineName}";
        var exists = await SendAsync(HttpMethod.Get, path, null, cancellationToken, HttpStatusCode.NotFound) is not null;

        if (exists && !force)
        {
            return false;
        }

        if (exists)
        {
            await SendAsync(HttpMethod.Delete, path, null, cancellationToken);
        }

        await SendAsync(HttpMethod.Put, path, IndexDefinitions.RrfPipeline(), cancellationToken);
        LogPipelineCreated(_options.RrfPipelineName);
        return true;
    }

    /// <summary>
    /// Searches chunks: hybrid (BM25 + k-NN fused by RRF) when <paramref name="embedding"/> is given, otherwise BM25.
    /// </summary>
    public async Task<SearchResult> SearchAsync(
        SearchQuery query, IReadOnlyList<float>? embedding = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (embedding is not { Count: > 0 })
        {
            var bm25 = await SendAsync(
                HttpMethod.Post, $"{IndexName}/_search", QueryBuilder.BuildBm25(query), cancellationToken);
            var hits = ReadHits(bm25!);
            return new SearchResult(bm25!["hits"]!["total"]!["value"]!.GetValue<long>(), hits, SearchModes.Bm25);
        }

        var body = QueryBuilder.BuildHybrid(query, embedding, _options.HybridSearchSizeMultiplier);
        var hybrid = await SendAsync(
            HttpMethod.Post,
            $"{IndexName}/_search?search_pipeline={Uri.EscapeDataString(_options.RrfPipelineName)}",
            body,
            cancellationToken);
        var filtered = ReadHits(hybrid!).Where(h => h.Score >= query.MinScore).ToList();
        return new SearchResult(filtered.Count, filtered, SearchModes.Hybrid);
    }

    /// <summary>
    /// Indexes chunks with <c>_id = {arxiv_id}:{chunk_index}</c> (C5) and waits for them to be searchable.
    /// Counts successes and failures from the per-item results.
    /// </summary>
    public async Task<BulkIndexResult> BulkIndexChunksAsync(
        IEnumerable<ChunkDocument> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var ndjson = new StringBuilder();
        var count = 0;
        foreach (var chunk in chunks)
        {
            var action = new JsonObject
            {
                ["index"] = new JsonObject { ["_index"] = IndexName, ["_id"] = chunk.ChunkId },
            };
            ndjson.Append(action.ToJsonString()).Append('\n');
            ndjson.Append(JsonSerializer.Serialize(ToSource(chunk), ApiJson.Options)).Append('\n');
            count++;
        }

        if (count == 0)
        {
            return new BulkIndexResult(0, 0, []);
        }

        using var content = new StringContent(ndjson.ToString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
        var response = await SendContentAsync(HttpMethod.Post, "_bulk?refresh=wait_for", content, cancellationToken);

        var succeeded = 0;
        var errors = new List<string>();
        foreach (var item in response!["items"]!.AsArray())
        {
            var result = item!["index"]!;
            var status = result["status"]!.GetValue<int>();
            if (status is >= 200 and < 300)
            {
                succeeded++;
            }
            else
            {
                errors.Add($"{result["_id"]}: {result["error"]?["reason"] ?? result["error"]?.ToJsonString()}");
            }
        }

        if (errors.Count > 0)
        {
            LogBulkFailures(errors.Count, errors[0]);
        }

        return new BulkIndexResult(succeeded, errors.Count, errors);
    }

    /// <summary>Deletes every chunk of a paper. Returns the number deleted.</summary>
    public async Task<long> DeletePaperChunksAsync(string arxivId, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["query"] = new JsonObject { ["term"] = new JsonObject { ["arxiv_id"] = arxivId } } };
        var response = await SendAsync(
            HttpMethod.Post, $"{IndexName}/_delete_by_query?refresh=true", body, cancellationToken);
        return response?["deleted"]?.GetValue<long>() ?? 0;
    }

    /// <summary>A paper's chunks in chunk order (up to 1000).</summary>
    public async Task<IReadOnlyList<ChunkHit>> GetChunksByPaperAsync(string arxivId, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["query"] = new JsonObject { ["term"] = new JsonObject { ["arxiv_id"] = arxivId } },
            ["size"] = 1000,
            ["sort"] = new JsonArray(new JsonObject { ["chunk_index"] = "asc" }),
            ["_source"] = new JsonObject { ["excludes"] = new JsonArray("embedding") },
        };
        var response = await SendAsync(HttpMethod.Post, $"{IndexName}/_search", body, cancellationToken);
        return ReadHits(response!);
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(HttpMethod.Get, $"{IndexName}/_count", null, cancellationToken);
        return response!["count"]!.GetValue<long>();
    }

    public async Task<long> CountUniquePapersAsync(CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["size"] = 0,
            ["aggs"] = new JsonObject { ["papers"] = new JsonObject { ["cardinality"] = new JsonObject { ["field"] = "arxiv_id" } } },
        };
        var response = await SendAsync(HttpMethod.Post, $"{IndexName}/_search", body, cancellationToken);
        return response!["aggregations"]!["papers"]!["value"]!.GetValue<long>();
    }

    private async Task<bool> IndexExistsAsync(CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Head, IndexName, null, cancellationToken, HttpStatusCode.NotFound) is not null;

    private static JsonObject ToSource(ChunkDocument chunk)
    {
        var source = JsonSerializer.SerializeToNode(chunk, ApiJson.Options)!.AsObject();
        source["chunk_id"] = chunk.ChunkId;
        return source;
    }

    private static List<ChunkHit> ReadHits(JsonNode response)
    {
        var hits = new List<ChunkHit>();
        foreach (var hit in response["hits"]!["hits"]!.AsArray())
        {
            var source = hit!["_source"]?.AsObject() ?? [];
            hits.Add(new ChunkHit(
                ChunkId: hit["_id"]!.GetValue<string>(),
                ArxivId: Text(source["arxiv_id"]) ?? string.Empty,
                Title: Text(source["title"]) ?? string.Empty,
                Authors: Text(source["authors"]),
                Abstract: Text(source["abstract"]),
                PublishedDate: Text(source["published_date"]),
                ChunkText: Text(source["chunk_text"]),
                ChunkIndex: source["chunk_index"]?.GetValue<int>(),
                SectionTitle: Text(source["section_title"]),
                Score: hit["_score"] is JsonValue score ? score.GetValue<double>() : 0,
                Highlights: ReadHighlights(hit["highlight"])));
        }

        return hits;
    }

    // Strings as stored; author lists from other indexers are joined like ours.
    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonArray array => string.Join(", ", array.Select(item => item?.ToString())),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    private static Dictionary<string, IReadOnlyList<string>>? ReadHighlights(JsonNode? highlight) =>
        highlight?.AsObject().ToDictionary(
            field => field.Key,
            field => (IReadOnlyList<string>)[.. field.Value!.AsArray().Select(fragment => fragment!.GetValue<string>())]);

    private Task<JsonNode?> SendAsync(
        HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, params HttpStatusCode[] allowed)
    {
        HttpContent? content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return SendContentAsync(method, path, content, cancellationToken, allowed);
    }

    /// <summary>
    /// Sends a request and returns the parsed JSON body, or null for an <paramref name="allowed"/> non-success status.
    /// </summary>
    private async Task<JsonNode?> SendContentAsync(
        HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken, params HttpStatusCode[] allowed)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new SearchUnavailableException($"OpenSearch is unreachable at {http.BaseAddress}: {ex.Message}", ex);
        }

        using (response)
        {
            if (allowed.Contains(response.StatusCode))
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var reason = ErrorReason(text) ?? response.ReasonPhrase ?? "unknown error";
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    throw new SearchUnavailableException($"OpenSearch is unavailable: {reason}");
                }

                throw new SearchQueryException((int)response.StatusCode, reason);
            }

            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text);
        }
    }

    // OpenSearch errors are {"error": {"type": ..., "reason": ...}, "status": ...} or {"error": "text"}.
    private static string? ErrorReason(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            return error switch
            {
                JsonObject obj => $"{obj["type"]}: {obj["reason"]}",
                JsonValue value => value.ToString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenSearch health check failed: {Reason}")]
    private partial void LogHealthCheckFailed(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created index {IndexName}")]
    private partial void LogIndexCreated(string indexName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted index {IndexName}")]
    private partial void LogIndexDeleted(string indexName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created search pipeline {PipelineName}")]
    private partial void LogPipelineCreated(string pipelineName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} chunks failed to index; first error: {FirstError}")]
    private partial void LogBulkFailures(int count, string firstError);
}

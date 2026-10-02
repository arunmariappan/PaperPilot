using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace PaperPilot.Infrastructure.Embeddings;

/// <summary>Jina AI embeddings (<c>jina-embeddings-v3</c>), with the same request body as the Python client.</summary>
internal sealed class JinaEmbeddingService(
    IHttpClientFactory httpClientFactory,
    IOptions<JinaOptions> jinaOptions,
    IOptions<OpenSearchOptions> searchOptions) : IEmbeddingService
{
    public const string QueryClientName = "jina-query";
    public const string PassageClientName = "jina-passages";

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var vectors = await EmbedAsync(QueryClientName, "retrieval.query", [query], cancellationToken);
        return vectors[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
        IReadOnlyList<string> passages, int batchSize = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var vectors = new List<float[]>(passages.Count);
        foreach (var batch in passages.Chunk(batchSize))
        {
            vectors.AddRange(await EmbedAsync(PassageClientName, "retrieval.passage", batch, cancellationToken));
        }

        return vectors;
    }

    private async Task<float[][]> EmbedAsync(
        string clientName, string task, string[] input, CancellationToken cancellationToken)
    {
        var jina = jinaOptions.Value;
        if (string.IsNullOrWhiteSpace(jina.ApiKey))
        {
            throw new EmbeddingUnavailableException("Jina:ApiKey is not set.");
        }

        var dimensions = searchOptions.Value.VectorDimension;
        var body = new JinaEmbeddingRequest(jina.Model, task, dimensions, LateChunking: false, EmbeddingType: "float", input);
        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings")
        {
            Content = JsonContent.Create(body, options: ApiJson.Options),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jina.ApiKey);

        JinaEmbeddingResponse? response;
        try
        {
            using var http = await httpClientFactory.CreateClient(clientName).SendAsync(request, cancellationToken);
            if (!http.IsSuccessStatusCode)
            {
                var error = await http.Content.ReadAsStringAsync(cancellationToken);
                throw new EmbeddingUnavailableException(
                    $"Jina returned {(int)http.StatusCode}: {(error.Length > 300 ? error[..300] : error)}");
            }

            response = await http.Content.ReadFromJsonAsync<JinaEmbeddingResponse>(ApiJson.Options, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new EmbeddingUnavailableException($"Jina embeddings request failed: {ex.Message}", ex);
        }

        var vectors = response?.Data?.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray() ?? [];
        if (vectors.Length != input.Length)
        {
            throw new EmbeddingUnavailableException($"Jina returned {vectors.Length} vectors for {input.Length} inputs.");
        }

        if (vectors.FirstOrDefault(v => v.Length != dimensions) is { } wrong)
        {
            throw new EmbeddingUnavailableException($"Jina returned a {wrong.Length}-dimension vector; expected {dimensions}.");
        }

        return vectors;
    }

    /// <summary>
    /// Retries only on 429, waiting for <c>Retry-After</c> when given, otherwise 5 s, 10 s, 20 s, … capped at 60 s.
    /// Each attempt has 30 s.
    /// </summary>
    internal static void AddRateLimitRetry(ResiliencePipelineBuilder<HttpResponseMessage> pipeline, int maxRetries) =>
        pipeline
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = maxRetries,
                ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.StatusCode == HttpStatusCode.TooManyRequests),
                DelayGenerator = args => ValueTask.FromResult<TimeSpan?>(
                    RetryAfter(args.Outcome.Result) ?? TimeSpan.FromSeconds(Math.Min(5 * Math.Pow(2, args.AttemptNumber), 60))),
            })
            .AddTimeout(TimeSpan.FromSeconds(30));

    private static TimeSpan? RetryAfter(HttpResponseMessage? response) =>
        response?.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
            _ => null,
        };
}

internal sealed record JinaEmbeddingRequest(
    string Model, string Task, int Dimensions, bool LateChunking, string EmbeddingType, IReadOnlyList<string> Input);

internal sealed record JinaEmbeddingResponse(string? Model, IReadOnlyList<JinaEmbedding>? Data);

internal sealed record JinaEmbedding(int Index, float[] Embedding);

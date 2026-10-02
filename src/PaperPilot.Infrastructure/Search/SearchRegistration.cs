using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Embeddings;
using PaperPilot.Infrastructure.Http;

namespace PaperPilot.Infrastructure.Search;

public static class SearchRegistration
{
    /// <summary>
    /// Registers <see cref="OpenSearchClient"/> and the Jina <see cref="IEmbeddingService"/>.
    /// Needs <c>AddPaperPilotOptions()</c>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotSearch(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Bulk indexing waits for a refresh, so attempts get 60 s. Retries are quick: an unreachable cluster should
        // turn into a 503 in about a second, not after the standard 2-4-8 s back-off.
        builder.Services.AddHttpClient<OpenSearchClient>((services, http) =>
                http.BaseAddress = WithTrailingSlash(services.GetRequiredService<IOptions<OpenSearchOptions>>().Value.Host))
            .ReplaceStandardResilienceHandler(options =>
            {
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
                options.Retry.MaxRetryAttempts = 2;
                options.Retry.Delay = TimeSpan.FromMilliseconds(250);
            });

        // Queries are interactive and can fall back to BM25, so they retry twice; indexing waits out the rate limit.
        builder.Services.AddHttpClient(JinaEmbeddingService.QueryClientName, ConfigureJina)
            .AddLongRunningResilienceHandler(
                JinaEmbeddingService.QueryClientName, TimeSpan.FromMinutes(2), p => JinaEmbeddingService.AddRateLimitRetry(p, 2));
        builder.Services.AddHttpClient(JinaEmbeddingService.PassageClientName, ConfigureJina)
            .AddLongRunningResilienceHandler(
                JinaEmbeddingService.PassageClientName, TimeSpan.FromMinutes(8), p => JinaEmbeddingService.AddRateLimitRetry(p, 6));
        builder.Services.AddSingleton<IEmbeddingService, JinaEmbeddingService>();

        return builder;
    }

    private static void ConfigureJina(IServiceProvider services, HttpClient http) =>
        http.BaseAddress = WithTrailingSlash(services.GetRequiredService<IOptions<JinaOptions>>().Value.BaseUrl);

    private static Uri WithTrailingSlash(string url) => new(url.TrimEnd('/') + "/");
}

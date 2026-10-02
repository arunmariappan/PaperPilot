using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PaperPilot.Infrastructure.Http;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;

namespace PaperPilot.Infrastructure;

/// <summary>Dependency checks for <c>/api/v1/health</c> (tag <see cref="ApiTag"/>) and Aspire's <c>/health</c>.</summary>
public static class HealthChecks
{
    /// <summary>Checks reported by <c>/api/v1/health</c>.</summary>
    public const string ApiTag = "api";

    internal const string OllamaHttpClient = "ollama-health";

    public static IHealthChecksBuilder AddDatabaseCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<DatabaseHealthCheck>("database", tags: [ApiTag]);

    public static IHealthChecksBuilder AddOpenSearchCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<OpenSearchHealthCheck>("opensearch", tags: [ApiTag]);

    /// <summary>
    /// Ollama runs outside Aspire, so a failure is reported as <see cref="HealthStatus.Degraded"/>: it shows up in
    /// <c>/api/v1/health</c> without marking the API itself unhealthy.
    /// </summary>
    public static IHealthChecksBuilder AddOllamaCheck(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpClient(OllamaHttpClient).WithoutResilience(TimeSpan.FromSeconds(5));
        return builder.AddCheck<OllamaHealthCheck>("ollama", failureStatus: HealthStatus.Degraded, tags: [ApiTag]);
    }
}

internal sealed class DatabaseHealthCheck(PaperPilotDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("Connected successfully");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or TimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, ex.Message, ex);
        }
    }
}

internal sealed class OpenSearchHealthCheck(OpenSearchClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!await client.HealthAsync(cancellationToken))
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Not responding");
        }

        try
        {
            var stats = await client.GetIndexStatsAsync(cancellationToken);
            return HealthCheckResult.Healthy($"Index '{stats.IndexName}' with {stats.DocumentCount} documents");
        }
        catch (Exception ex) when (ex is Core.Exceptions.SearchUnavailableException or Core.Exceptions.SearchQueryException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, ex.Message, ex);
        }
    }
}

internal sealed class OllamaHealthCheck(IHttpClientFactory httpClientFactory, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var endpoint = OllamaEndpoint.FromConfiguration(configuration);
        if (endpoint is null)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "ConnectionStrings:ollama is not set");
        }

        try
        {
            using var response = await httpClientFactory.CreateClient(HealthChecks.OllamaHttpClient)
                .GetAsync(new Uri(endpoint, "api/version"), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Ollama service is running")
                : new HealthCheckResult(context.Registration.FailureStatus, $"Ollama returned status {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, $"Cannot connect to Ollama service: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Ollama service timeout", ex);
        }
    }
}

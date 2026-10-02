using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaperPilot.Core.Exceptions;

namespace PaperPilot.Infrastructure.Search;

/// <summary>
/// At startup: checks OpenSearch, creates the chunk index and RRF pipeline if missing, and logs the document count.
/// If OpenSearch is down it logs a warning and lets the service start anyway, as the Python API did.
/// </summary>
public sealed partial class SearchIndexInitializer(OpenSearchClient client, ILogger<SearchIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await client.HealthAsync(cancellationToken))
        {
            LogUnavailable();
            return;
        }

        try
        {
            var created = await client.EnsureIndexAsync(force: false, cancellationToken);
            await client.EnsureRrfPipelineAsync(force: false, cancellationToken);
            var count = await client.CountAsync(cancellationToken);
            LogReady(client.IndexName, created ? "created" : "already existed", count);
        }
        catch (Exception ex) when (ex is SearchUnavailableException or SearchQueryException)
        {
            LogSetupFailed(ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "OpenSearch connection failed - search features will be limited")]
    private partial void LogUnavailable();

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenSearch ready: index {IndexName} {State}, {Count} documents indexed")]
    private partial void LogReady(string indexName, string state, long count);

    [LoggerMessage(Level = LogLevel.Error, Message = "OpenSearch index setup failed")]
    private partial void LogSetupFailed(Exception exception);
}

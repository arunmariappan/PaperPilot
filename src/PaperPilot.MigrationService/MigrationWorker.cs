namespace PaperPilot.MigrationService;

/// <summary>
/// Applies EF Core migrations and stops the host, so Api and Worker can start (<c>WaitForCompletion</c> in the AppHost).
/// The DbContext and the first migration arrive in phase 1.
/// </summary>
internal sealed partial class MigrationWorker(ILogger<MigrationWorker> logger, IHostApplicationLifetime lifetime)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogNoMigrations();
        lifetime.StopApplication();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No migrations to apply yet")]
    private partial void LogNoMigrations();
}

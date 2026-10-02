using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using PaperPilot.Infrastructure.Persistence;

namespace PaperPilot.MigrationService;

/// <summary>
/// Applies EF Core migrations and stops the host, so Api and Worker can start (<c>WaitForCompletion</c> in the AppHost).
/// A failure sets a non-zero exit code, which keeps them from starting against an unmigrated database.
/// </summary>
internal sealed partial class MigrationWorker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "PaperPilot.MigrationService";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Migrating database", ActivityKind.Client);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>();

            // The Aspire retry strategy re-runs the whole migration if Postgres is still starting.
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(ct => db.Database.MigrateAsync(ct), stoppingToken);

            LogMigrated();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.AddException(ex);
            LogFailed(ex);
            Environment.ExitCode = 1;
        }

        lifetime.StopApplication();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database is up to date")]
    private partial void LogMigrated();

    [LoggerMessage(Level = LogLevel.Error, Message = "Database migration failed")]
    private partial void LogFailed(Exception exception);
}

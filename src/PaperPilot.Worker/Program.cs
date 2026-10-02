using System.Globalization;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Http.HttpResults;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;
using PaperPilot.Ingestion;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddPaperPilotOptions();
builder.AddPaperPilotDatabase();
builder.AddPaperPilotSearch();
builder.AddPaperPilotIngestion();

builder.Services.AddHostedService<SearchIndexInitializer>();
builder.Services.AddHealthChecks().AddOpenSearchCheck();

// Hangfire keeps its tables in the "hangfire" schema of the papers database. A run can take longer than the default
// 30-minute invisibility timeout, after which another worker would pick it up a second time (R7).
var connectionString = builder.Configuration.GetConnectionString(PersistenceRegistration.ConnectionName)
    ?? throw new InvalidOperationException($"ConnectionStrings:{PersistenceRegistration.ConnectionName} is not set.");
builder.Services.AddHangfire(hangfire => hangfire
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(storage => storage.UseNpgsqlConnection(connectionString), new PostgreSqlStorageOptions
    {
        SchemaName = "hangfire",
        PrepareSchemaIfNecessary = true,
        InvisibilityTimeout = TimeSpan.FromHours(3),
    }));
builder.Services.AddHangfireServer(server => server.WorkerCount = 1);

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapHangfireDashboard("/hangfire");

app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<DailyIngestionJob>(
    DailyIngestionJob.RecurringJobId,
    job => job.RunAsync(null, null, IngestionTriggers.Scheduled, null, CancellationToken.None),
    DailyIngestionJob.Schedule,
    new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

var ingestion = app.MapGroup("/ingestion").WithTags("ingestion");

// N3: a manual run or backfill, e.g. POST /ingestion/run?from=20261001&to=20261003. Without dates it uses the
// scheduled window.
ingestion.MapPost("/run", Results<Accepted<RunQueued>, ProblemHttpResult> (string? from, string? to, IBackgroundJobClient jobs) =>
{
    foreach (var (name, value) in new[] { ("from", from), ("to", to) })
    {
        if (value is not null && !DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return TypedResults.Problem($"'{name}' must be a yyyyMMdd date.", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    if (from is not null && to is not null && string.CompareOrdinal(from, to) > 0)
    {
        return TypedResults.Problem("'from' must not be after 'to'.", statusCode: StatusCodes.Status400BadRequest);
    }

    var jobId = jobs.Enqueue<DailyIngestionJob>(job => job.RunAsync(from, to, IngestionTriggers.Manual, null, CancellationToken.None));
    return TypedResults.Accepted("/hangfire/jobs/details/" + jobId, new RunQueued(jobId));
});

// N2: the newest runs first.
ingestion.MapGet("/runs", async (IngestionRunRepository runs, int? limit, CancellationToken cancellationToken) =>
    TypedResults.Ok(await runs.ListAsync(Math.Clamp(limit ?? 20, 1, 200), cancellationToken)));

app.Run();

internal sealed record RunQueued(string JobId);

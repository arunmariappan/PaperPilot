using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Pdf;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;

namespace PaperPilot.Ingestion;

/// <summary>
/// The daily ingestion job: setup → fetch → index → report → cleanup, as one Hangfire job (C9). Every run writes an
/// <c>ingestion_runs</c> row (N2), and the job fails visibly when nothing useful happened (B15).
/// </summary>
public sealed partial class DailyIngestionJob(
    PaperFetchService fetch,
    HybridIndexer indexer,
    IngestionRunRepository runs,
    PaperRepository papers,
    OpenSearchClient search,
    DoclingServeClient docling,
    IOptions<JinaOptions> jina,
    IOptions<ArxivOptions> arxiv,
    TimeProvider time,
    ILogger<DailyIngestionJob> logger)
{
    public const string RecurringJobId = "arxiv-daily-ingestion";

    /// <summary>Weekdays at 06:00 UTC, as the Airflow DAG ran.</summary>
    public const string Schedule = "0 6 * * 1-5";

    public const string ActivitySourceName = "PaperPilot.Ingestion";

    private static readonly ActivitySource Source = new(ActivitySourceName);
    private static readonly Meter Meter = new(ActivitySourceName);
    private static readonly Counter<long> PapersFetched = Meter.CreateCounter<long>("paperpilot.ingestion.papers_fetched");
    private static readonly Counter<long> PdfsParsed = Meter.CreateCounter<long>("paperpilot.ingestion.pdfs_parsed");
    private static readonly Counter<long> PapersIndexed = Meter.CreateCounter<long>("paperpilot.ingestion.papers_indexed");
    private static readonly Counter<long> ChunksIndexed = Meter.CreateCounter<long>("paperpilot.ingestion.chunks_indexed");

    /// <summary>
    /// Runs one ingestion. <paramref name="from"/> and <paramref name="to"/> (<c>yyyyMMdd</c>) override the target window.
    /// Hangfire passes <paramref name="context"/> and retries a failed run twice, 5 minutes apart; runs never overlap.
    /// </summary>
    /// <exception cref="IngestionFailedException">A setup check failed, or the run did nothing useful.</exception>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 300])]
    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task RunAsync(string? from, string? to, string trigger, PerformContext? context, CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var (targetFrom, targetTo) = IngestionRules.TargetWindow(
            ParseDate(from), ParseDate(to), started, await runs.GetLastSucceededTargetAsync(cancellationToken));
        var run = new IngestionRun
        {
            TargetFrom = targetFrom,
            TargetTo = targetTo,
            Trigger = trigger,
            StartedAt = started,
            Status = IngestionRunStatus.Running,
            HangfireJobId = context?.BackgroundJob?.Id,
        };
        await runs.SaveAsync(run, cancellationToken);

        using var activity = Source.StartActivity("ingestion_run");
        activity?.SetTag("ingestion.target_from", targetFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        activity?.SetTag("ingestion.target_to", targetTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        activity?.SetTag("ingestion.trigger", trigger);
        LogStarting(logger, targetFrom, targetTo, trigger);

        string? failure;
        try
        {
            failure = await ExecuteAsync(run, cancellationToken);
        }
        catch (Exception ex)
        {
            run.Errors.Add(ex.Message);
            await ReportAsync(run, ex.Message, activity);
            throw;
        }

        await ReportAsync(run, failure, activity);
        using (Source.StartActivity("cleanup"))
        {
            var deleted = IngestionRules.CleanPdfCache(arxiv.Value.PdfCacheDir, time.GetUtcNow());
            LogCleanedUp(logger, deleted);
        }

        if (failure is not null)
        {
            throw new IngestionFailedException(failure);
        }
    }

    /// <summary>Setup, fetch and index. Returns the B15 failure reason, or null.</summary>
    private async Task<string?> ExecuteAsync(IngestionRun run, CancellationToken cancellationToken)
    {
        using (Source.StartActivity("setup"))
        {
            await SetupAsync(cancellationToken);
        }

        FetchResult fetched;
        using (Source.StartActivity("fetch"))
        {
            fetched = await fetch.FetchAndStoreAsync(run.TargetFrom, run.TargetTo, cancellationToken);
        }

        run.PapersFetched = fetched.PapersFetched;
        run.PdfsDownloaded = fetched.PdfsDownloaded;
        run.PdfsParsed = fetched.PdfsParsed;
        run.PdfsSkipped = fetched.PdfsSkipped;
        run.PapersStored = fetched.PapersStored;
        run.Errors.AddRange(fetched.Errors);
        PapersFetched.Add(fetched.PapersFetched);
        PdfsParsed.Add(fetched.PdfsParsed);

        IndexResult indexed;
        using (Source.StartActivity("index"))
        {
            indexed = await indexer.IndexPapersAsync(fetched.UpsertedPaperIds, replaceExisting: true, cancellationToken);
        }

        run.ChunksCreated = indexed.ChunksCreated;
        run.ChunksIndexed = indexed.ChunksIndexed;
        run.EmbeddingsGenerated = indexed.EmbeddingsGenerated;
        run.Errors.AddRange(indexed.Errors);
        PapersIndexed.Add(indexed.PapersWithContent);
        ChunksIndexed.Add(indexed.ChunksIndexed);

        return IngestionRules.FailureReason(run, indexed.PapersWithContent);
    }

    /// <summary>Checks every dependency first, so a run fails fast with a clear message.</summary>
    private async Task SetupAsync(CancellationToken cancellationToken)
    {
        await papers.CountAsync(cancellationToken);

        if (!await search.HealthAsync(cancellationToken))
        {
            throw new IngestionFailedException("Setup failed: OpenSearch is not reachable");
        }

        await search.EnsureIndexAsync(cancellationToken: cancellationToken);
        await search.EnsureRrfPipelineAsync(cancellationToken: cancellationToken);

        if (!await docling.HealthAsync(cancellationToken))
        {
            throw new IngestionFailedException("Setup failed: docling-serve is not reachable");
        }

        if (string.IsNullOrWhiteSpace(jina.Value.ApiKey))
        {
            throw new IngestionFailedException("Setup failed: Jina:ApiKey is not set, so chunks can't be embedded");
        }
    }

    /// <summary>Writes the run row (always, even when the run was cancelled) and logs a JSON summary like Python's report.</summary>
    private async Task ReportAsync(IngestionRun run, string? failure, Activity? activity)
    {
        using var _ = Source.StartActivity("report");
        run.FinishedAt = time.GetUtcNow();
        run.Status = failure is null ? IngestionRunStatus.Succeeded : IngestionRunStatus.Failed;

        IndexStats? stats = null;
        try
        {
            stats = await search.GetIndexStatsAsync(CancellationToken.None);
            run.IndexDocCountAfter = stats.DocumentCount;
        }
        catch (Exception ex) when (ex is SearchUnavailableException or SearchQueryException)
        {
            LogStatsUnavailable(logger, ex.Message);
        }

        await runs.SaveAsync(run, CancellationToken.None);

        activity?.SetTag("ingestion.status", run.Status);
        if (failure is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure);
        }

        var summary = new
        {
            run.Id,
            run.Status,
            run.Trigger,
            run.TargetFrom,
            run.TargetTo,
            Fetch = new { run.PapersFetched, run.PdfsDownloaded, run.PdfsParsed, run.PdfsSkipped, run.PapersStored },
            Indexing = new { run.ChunksCreated, run.ChunksIndexed, run.EmbeddingsGenerated },
            Database = new { TotalPapers = await papers.CountAsync(CancellationToken.None) },
            OpenSearch = stats is null ? null : new
            {
                stats.IndexName,
                stats.DocumentCount,
                IndexSizeMb = Math.Round(stats.SizeInBytes / 1024.0 / 1024, 2),
            },
            Errors = run.Errors.Count,
            Failure = failure,
        };
        var report = JsonSerializer.Serialize(summary, ApiJson.Options);
        LogReport(logger, report);
    }

    private static DateOnly? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? date
                : throw new ArgumentException($"'{value}' is not a yyyyMMdd date.", nameof(value));

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting {Trigger} ingestion for papers submitted {From} to {To}")]
    private static partial void LogStarting(ILogger logger, DateOnly from, DateOnly to, string trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Daily ingestion report: {Report}")]
    private static partial void LogReport(ILogger logger, string report);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read OpenSearch statistics for the report: {Reason}")]
    private static partial void LogStatsUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} cached PDFs older than 30 days")]
    private static partial void LogCleanedUp(ILogger logger, int count);
}

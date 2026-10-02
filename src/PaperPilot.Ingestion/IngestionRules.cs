using PaperPilot.Core.Domain;

namespace PaperPilot.Ingestion;

/// <summary>The pure decisions of the ingestion job, kept separate so they can be unit-tested.</summary>
public static class IngestionRules
{
    /// <summary>A run never reaches back more than this many days, so a Monday run covers Friday to Sunday.</summary>
    public const int MaxCatchUpDays = 3;

    /// <summary>Cached PDFs older than this are deleted after each run (B14).</summary>
    public static readonly TimeSpan PdfRetention = TimeSpan.FromDays(30);

    /// <summary>
    /// The submission dates to fetch (B25). Explicit dates win; one given date means that single day. Otherwise the
    /// window ends yesterday, relative to the actual run time in UTC, and starts the day after the last succeeded run
    /// covered, but no more than <see cref="MaxCatchUpDays"/> days back. Without a previous success it is yesterday.
    /// </summary>
    public static (DateOnly From, DateOnly To) TargetWindow(
        DateOnly? from, DateOnly? to, DateTimeOffset now, DateOnly? lastSucceededTo)
    {
        if (from is not null || to is not null)
        {
            var start = from ?? to!.Value;
            var end = to ?? from!.Value;
            return start <= end
                ? (start, end)
                : throw new ArgumentException($"The window starts ({start:yyyy-MM-dd}) after it ends ({end:yyyy-MM-dd}).");
        }

        var yesterday = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);
        var earliest = yesterday.AddDays(-(MaxCatchUpDays - 1));
        var first = lastSucceededTo is { } last ? last.AddDays(1) : yesterday;
        if (first < earliest)
        {
            first = earliest;
        }

        return (first > yesterday ? yesterday : first, yesterday);
    }

    /// <summary>
    /// Why the run counts as failed even though no step threw (B15), or null when it did something useful: PDFs that
    /// should have been parsed (fetched and not skipped) but none were, or papers with content but nothing indexed.
    /// </summary>
    public static string? FailureReason(IngestionRun run, int papersWithContent)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.PapersFetched > run.PdfsSkipped && run.PdfsParsed == 0)
        {
            return $"Fetched {run.PapersFetched} papers but parsed none of their PDFs";
        }

        if (papersWithContent > 0 && run.ChunksIndexed == 0)
        {
            return $"{papersWithContent} papers had content but no chunks were indexed";
        }

        return null;
    }

    /// <summary>Deletes PDFs older than <see cref="PdfRetention"/> from the cache directory. Returns how many went.</summary>
    public static int CleanPdfCache(string directory, DateTimeOffset now)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.pdf"))
        {
            if (now - file.LastWriteTimeUtc > PdfRetention)
            {
                file.Delete();
                deleted++;
            }
        }

        return deleted;
    }
}

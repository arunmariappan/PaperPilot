using PaperPilot.Core.Domain;
using PaperPilot.Ingestion;

namespace PaperPilot.UnitTests.Ingestion;

public sealed class IngestionRulesTests : IDisposable
{
    private static readonly DateTimeOffset MondayMorning = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private readonly string _cache = Directory.CreateTempSubdirectory("paperpilot-cache-").FullName;

    [Fact]
    public void The_first_run_targets_yesterday() =>
        IngestionRules.TargetWindow(null, null, MondayMorning, lastSucceededTo: null)
            .ShouldBe((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 4)));

    [Fact]
    public void A_monday_run_after_a_friday_run_covers_friday_to_sunday() => // B25: Python never fetched Fri/Sat
        IngestionRules.TargetWindow(null, null, MondayMorning, lastSucceededTo: new DateOnly(2026, 10, 1))
            .ShouldBe((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 4)));

    [Fact]
    public void Yesterday_is_relative_to_the_actual_run_time_in_utc() =>
        IngestionRules.TargetWindow(null, null, new DateTimeOffset(2026, 10, 6, 1, 30, 0, TimeSpan.FromHours(5)), null)
            .ShouldBe((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 4))); // 2026-10-05 20:30 UTC

    [Fact]
    public void A_long_gap_is_capped_at_three_days() =>
        IngestionRules.TargetWindow(null, null, MondayMorning, lastSucceededTo: new DateOnly(2026, 9, 1))
            .ShouldBe((new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 4)));

    [Fact]
    public void A_second_run_on_the_same_day_fetches_yesterday_again() =>
        IngestionRules.TargetWindow(null, null, MondayMorning, lastSucceededTo: new DateOnly(2026, 10, 4))
            .ShouldBe((new DateOnly(2026, 10, 4), new DateOnly(2026, 10, 4)));

    [Fact]
    public void Explicit_dates_win_and_one_date_means_that_day()
    {
        var day = new DateOnly(2026, 9, 1);

        IngestionRules.TargetWindow(day, day.AddDays(4), MondayMorning, new DateOnly(2026, 10, 1)).ShouldBe((day, day.AddDays(4)));
        IngestionRules.TargetWindow(day, null, MondayMorning, null).ShouldBe((day, day));
        IngestionRules.TargetWindow(null, day, MondayMorning, null).ShouldBe((day, day));
        Should.Throw<ArgumentException>(() => IngestionRules.TargetWindow(day.AddDays(1), day, MondayMorning, null));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0, null)]
    [InlineData(5, 3, 0, 2, 20, null)]
    [InlineData(5, 0, 0, 0, 0, "Fetched 5 papers but parsed none of their PDFs")]
    [InlineData(2, 0, 2, 0, 0, null)] // every PDF over the limits: nothing failed
    [InlineData(5, 3, 0, 3, 0, "3 papers had content but no chunks were indexed")]
    public void A_run_that_did_nothing_useful_fails(
        int fetched, int parsed, int skipped, int papersWithContent, int chunksIndexed, string? reason) // B15
    {
        var run = new IngestionRun
        {
            Trigger = IngestionTriggers.Scheduled,
            Status = IngestionRunStatus.Running,
            PapersFetched = fetched,
            PdfsParsed = parsed,
            PdfsSkipped = skipped,
            ChunksIndexed = chunksIndexed,
        };

        IngestionRules.FailureReason(run, papersWithContent).ShouldBe(reason);
    }

    [Fact]
    public void Only_pdfs_older_than_30_days_are_cleaned_up() // B14: Python looked in /tmp instead
    {
        var now = new DateTimeOffset(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(Write("old.pdf"), now.AddDays(-31).UtcDateTime);
        File.SetLastWriteTimeUtc(Write("recent.pdf"), now.AddDays(-29).UtcDateTime);
        File.SetLastWriteTimeUtc(Write("old.txt"), now.AddDays(-90).UtcDateTime);

        IngestionRules.CleanPdfCache(_cache, now).ShouldBe(1);

        Directory.GetFiles(_cache).Select(Path.GetFileName).ShouldBe(["old.txt", "recent.pdf"], ignoreOrder: true);
        IngestionRules.CleanPdfCache(Path.Combine(_cache, "missing"), now).ShouldBe(0);
    }

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private string Write(string name)
    {
        var path = Path.Combine(_cache, name);
        File.WriteAllText(path, "x");
        return path;
    }
}

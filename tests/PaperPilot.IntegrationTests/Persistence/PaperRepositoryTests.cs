using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaperPilot.Core.Domain;
using PaperPilot.Infrastructure.Persistence;

namespace PaperPilot.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class PaperRepositoryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Published = new(2026, 9, 30, 17, 59, 59, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await WithDbAsync(db => db.Papers.ExecuteDeleteAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_paper_without_parsed_content_is_stored_as_metadata_only()
    {
        var now = postgres.Clock.GetUtcNow();

        var stored = await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00001v1"), null), Ct));

        var paper = (await WithRepositoryAsync(r => r.GetByIdAsync(stored.Id, Ct)))!;
        paper.ArxivId.ShouldBe("2510.00001v1");
        paper.Authors.ShouldBe(["Ashish Vaswani", "Noam Shazeer"]);
        paper.Categories.ShouldBe(["cs.CL", "cs.LG"]);
        paper.PdfProcessed.ShouldBeFalse();
        paper.RawText.ShouldBeNull();
        paper.Sections.ShouldBeNull();
        paper.ParserMetadata!.RootElement.GetProperty("note").GetString().ShouldBe(PaperRepository.ParseFailedNote);
        (paper.CreatedAt, paper.UpdatedAt).ShouldBe((now, now));
    }

    [Fact]
    public async Task Upserting_an_existing_paper_updates_its_metadata()
    {
        var first = await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00002v1"), null), Ct));
        postgres.Clock.Advance(TimeSpan.FromHours(1));

        var second = await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00002v1") with { Title = "Revised title" }, null), Ct));

        second.Id.ShouldBe(first.Id);
        var paper = (await WithRepositoryAsync(r => r.GetByArxivIdAsync("2510.00002v1", Ct)))!;
        paper.Title.ShouldBe("Revised title");
        paper.CreatedAt.ShouldBe(first.CreatedAt);
        paper.UpdatedAt.ShouldBe(postgres.Clock.GetUtcNow());
        (await WithRepositoryAsync(r => r.CountAsync(Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task Parsed_content_round_trips_through_jsonb()
    {
        await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00003v1"), Parsed("Full text")), Ct));

        var paper = (await WithRepositoryAsync(r => r.GetByArxivIdAsync("2510.00003v1", Ct)))!;
        paper.PdfProcessed.ShouldBeTrue();
        paper.PdfProcessingDate.ShouldBe(postgres.Clock.GetUtcNow());
        paper.RawText.ShouldBe("Full text");
        paper.ParserUsed.ShouldBe("docling");
        paper.References.ShouldBe([]);
        paper.Sections.ShouldBe([new PaperSection("Introduction", "Intro text"), new PaperSection("Method", "Method text", 2)]);
        paper.ParserMetadata!.RootElement.GetProperty("source").GetString().ShouldBe("docling");

        // The JSON keys are lowercase, as in the Python table.
        var firstTitle = await WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT sections->0->>'title' AS \"Value\" FROM papers WHERE arxiv_id = '2510.00003v1'")
            .SingleAsync(Ct));
        firstTitle.ShouldBe("Introduction");
    }

    [Fact]
    public async Task A_failed_reparse_keeps_the_earlier_parsed_content() // B23
    {
        await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00004v1"), Parsed("Original text")), Ct));
        var parsedAt = postgres.Clock.GetUtcNow();
        postgres.Clock.Advance(TimeSpan.FromDays(1));

        await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00004v1") with { Title = "Revised title" }, Content: null), Ct));

        var paper = (await WithRepositoryAsync(r => r.GetByArxivIdAsync("2510.00004v1", Ct)))!;
        paper.Title.ShouldBe("Revised title");
        paper.PdfProcessed.ShouldBeTrue();
        paper.RawText.ShouldBe("Original text");
        paper.Sections!.Count.ShouldBe(2);
        paper.ParserUsed.ShouldBe("docling");
        paper.ParserMetadata!.RootElement.GetProperty("source").GetString().ShouldBe("docling");
        paper.PdfProcessingDate.ShouldBe(parsedAt);
    }

    [Fact]
    public async Task A_successful_reparse_replaces_the_content()
    {
        await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00005v1"), Parsed("Old text")), Ct));
        postgres.Clock.Advance(TimeSpan.FromDays(1));

        await WithRepositoryAsync(r => r.UpsertAsync(new PaperUpsert(Metadata("2510.00005v1"), Parsed("New text")), Ct));

        var paper = (await WithRepositoryAsync(r => r.GetByArxivIdAsync("2510.00005v1", Ct)))!;
        paper.RawText.ShouldBe("New text");
        paper.PdfProcessingDate.ShouldBe(postgres.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Timestamps_are_stored_in_utc() // B21
    {
        var india = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(5.5));

        await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00006v1") with { PublishedDate = india }, null), Ct));

        var paper = (await WithRepositoryAsync(r => r.GetByArxivIdAsync("2510.00006v1", Ct)))!;
        paper.PublishedDate.Offset.ShouldBe(TimeSpan.Zero);
        paper.PublishedDate.UtcDateTime.ShouldBe(new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc));
        paper.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Queries_and_processing_stats_match_the_Python_repository()
    {
        var a = await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00007v1") with { PublishedDate = Published.AddDays(-2) }, Parsed("a")), Ct));
        postgres.Clock.Advance(TimeSpan.FromMinutes(1));
        var b = await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00008v1") with { PublishedDate = Published.AddDays(-1) }, Parsed("b")), Ct));
        var c = await WithRepositoryAsync(r => r.UpsertAsync(
            new PaperUpsert(Metadata("2510.00009v1") with { PublishedDate = Published }, null), Ct));

        (await WithRepositoryAsync(r => r.GetAllAsync(cancellationToken: Ct))).Select(p => p.Id).ShouldBe([c.Id, b.Id, a.Id]);
        (await WithRepositoryAsync(r => r.GetAllAsync(limit: 1, offset: 1, cancellationToken: Ct))).Single().Id.ShouldBe(b.Id);
        (await WithRepositoryAsync(r => r.GetProcessedAsync(cancellationToken: Ct))).Select(p => p.Id).ShouldBe([b.Id, a.Id]);
        (await WithRepositoryAsync(r => r.GetUnprocessedAsync(cancellationToken: Ct))).Select(p => p.Id).ShouldBe([c.Id]);
        (await WithRepositoryAsync(r => r.GetWithRawTextAsync(cancellationToken: Ct))).Select(p => p.Id).ShouldBe([b.Id, a.Id]);
        (await WithRepositoryAsync(r => r.GetByIdsAsync([a.Id, c.Id, Guid.NewGuid()], Ct)))
            .Select(p => p.Id).ShouldBe([a.Id, c.Id], ignoreOrder: true);

        var stats = await WithRepositoryAsync(r => r.GetProcessingStatsAsync(Ct));
        (stats.TotalPapers, stats.ProcessedPapers, stats.PapersWithText).ShouldBe((3, 2, 2));
        stats.ProcessingRate.ShouldBe(200.0 / 3, tolerance: 1e-9);
        stats.TextExtractionRate.ShouldBe(100.0);
    }

    [Fact]
    public async Task Stats_on_an_empty_table_are_zero()
    {
        var stats = await WithRepositoryAsync(r => r.GetProcessingStatsAsync(Ct));

        stats.ShouldBe(new PaperProcessingStats(0, 0, 0, 0, 0));
    }

    private static ArxivPaper Metadata(string arxivId) => new(
        arxivId,
        "Attention Is All You Need",
        ["Ashish Vaswani", "Noam Shazeer"],
        "The dominant sequence transduction models are based on complex recurrent or convolutional neural networks.",
        ["cs.CL", "cs.LG"],
        Published,
        $"https://arxiv.org/pdf/{arxivId}");

    private static PdfContent Parsed(string rawText) => new(
        [new PaperSection("Introduction", "Intro text"), new PaperSection("Method", "Method text", 2)],
        rawText,
        "docling",
        new Dictionary<string, object?>
        {
            ["source"] = "docling",
            ["note"] = "Content extracted from PDF, metadata comes from arXiv API",
        });

    // Each call gets its own scope (and pooled DbContext), like separate requests or job steps.
    private async Task<T> WithRepositoryAsync<T>(Func<PaperRepository, Task<T>> action)
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PaperRepository>());
    }

    private async Task<T> WithDbAsync<T>(Func<PaperPilotDbContext, Task<T>> action)
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>());
    }
}

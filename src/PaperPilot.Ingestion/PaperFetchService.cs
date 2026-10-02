using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Arxiv;
using PaperPilot.Infrastructure.Pdf;
using PaperPilot.Infrastructure.Persistence;

namespace PaperPilot.Ingestion;

/// <summary>What the fetch step did.</summary>
/// <param name="UpsertedPaperIds">Exactly the papers stored by this run, for the index step (B13).</param>
/// <param name="PdfsSkipped">PDFs over the size or page limit; their papers are stored as metadata only.</param>
public sealed record FetchResult(
    int PapersFetched,
    int PdfsDownloaded,
    int PdfsParsed,
    int PdfsSkipped,
    int PapersStored,
    IReadOnlyList<Guid> UpsertedPaperIds,
    IReadOnlyList<string> Errors);

/// <summary>
/// Fetches papers from arXiv, downloads and parses their PDFs, and stores them, as Python's <c>MetadataFetcher</c>
/// did. Downloads and parses overlap: each PDF is parsed as soon as it is downloaded. A failure for one paper is
/// recorded in <see cref="FetchResult.Errors"/> and never stops the others.
/// </summary>
public sealed partial class PaperFetchService(
    ArxivClient arxiv, PdfParser parser, PaperRepository papers, IOptions<ArxivOptions> options, ILogger<PaperFetchService> logger)
{
    /// <exception cref="ArxivApiException">The arXiv query failed; nothing was fetched.</exception>
    public async Task<FetchResult> FetchAndStoreAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var fetched = await arxiv.FetchPapersAsync(options.Value.MaxResults, from, to, cancellationToken);
        if (fetched.Count == 0)
        {
            LogNoPapers(logger, from, to);
            return new FetchResult(0, 0, 0, 0, 0, [], []);
        }

        using var downloads = new SemaphoreSlim(options.Value.MaxConcurrentDownloads);
        using var parses = new SemaphoreSlim(options.Value.MaxConcurrentParsing);
        var outcomes = await Task.WhenAll(fetched.Select(p => DownloadAndParseAsync(p, downloads, parses, cancellationToken)));

        var errors = outcomes.Where(o => o.Error is not null).Select(o => o.Error!).ToList();
        var ids = new List<Guid>();
        foreach (var (paper, outcome) in fetched.Zip(outcomes))
        {
            try
            {
                var stored = await papers.UpsertAsync(new PaperUpsert(paper, outcome.Content), cancellationToken);
                ids.Add(stored.Id);
            }
            catch (DbUpdateException ex)
            {
                errors.Add($"Failed to store paper {paper.ArxivId}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        var result = new FetchResult(
            fetched.Count,
            outcomes.Count(o => o.Downloaded),
            outcomes.Count(o => o.Content is not null),
            outcomes.Count(o => o.Skipped),
            ids.Count,
            ids,
            errors);
        LogFetched(logger, result.PapersFetched, result.PdfsDownloaded, result.PdfsParsed, result.PdfsSkipped, result.PapersStored, errors.Count);
        return result;
    }

    private async Task<PdfOutcome> DownloadAndParseAsync(
        ArxivPaper paper, SemaphoreSlim downloads, SemaphoreSlim parses, CancellationToken cancellationToken)
    {
        string? path;
        await downloads.WaitAsync(cancellationToken);
        try
        {
            path = await arxiv.DownloadPdfAsync(paper, cancellationToken: cancellationToken);
        }
        catch (PdfDownloadException ex)
        {
            return new PdfOutcome(Error: $"Download failed: {paper.ArxivId}: {ex.Message}");
        }
        finally
        {
            downloads.Release();
        }

        if (path is null)
        {
            return new PdfOutcome(Error: $"Download failed: {paper.ArxivId}: the paper has no PDF link");
        }

        await parses.WaitAsync(cancellationToken);
        try
        {
            var result = await parser.ParseAsync(path, cancellationToken);
            return new PdfOutcome(Downloaded: true, Content: result.Content, Skipped: result.SkipReason is not null);
        }
        catch (PdfParsingException ex)
        {
            return new PdfOutcome(Downloaded: true, Error: $"PDF parse failed: {paper.ArxivId}: {ex.Message}");
        }
        finally
        {
            parses.Release();
        }
    }

    private sealed record PdfOutcome(bool Downloaded = false, PdfContent? Content = null, bool Skipped = false, string? Error = null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No papers found between {From} and {To}")]
    private static partial void LogNoPapers(ILogger logger, DateOnly from, DateOnly to);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Fetch complete: {Fetched} papers, {Downloaded} PDFs downloaded, {Parsed} parsed, {Skipped} skipped, {Stored} stored, {Errors} errors")]
    private static partial void LogFetched(ILogger logger, int fetched, int downloaded, int parsed, int skipped, int stored, int errors);
}

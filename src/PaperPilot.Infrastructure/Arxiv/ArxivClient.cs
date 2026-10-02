using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Arxiv;

/// <summary>
/// arXiv API queries and PDF downloads. Every request goes through the shared <see cref="ArxivRateLimiter"/>.
/// Timeouts are per operation (<c>Arxiv:TimeoutSeconds</c>); for downloads it is an idle timeout between reads, so a
/// large PDF on a slow line isn't cut off while data keeps arriving.
/// </summary>
public sealed partial class ArxivClient(
    HttpClient http, IOptions<ArxivOptions> options, TimeProvider time, ILogger<ArxivClient> logger)
{
    private ArxivOptions Options => options.Value;

    private TimeSpan Timeout => TimeSpan.FromSeconds(Options.TimeoutSeconds);

    /// <summary>The newest <c>SearchCategory</c> papers, optionally submitted between <paramref name="from"/> and <paramref name="to"/>.</summary>
    /// <exception cref="ArxivApiException">The API failed, timed out or returned unreadable XML.</exception>
    public async Task<IReadOnlyList<ArxivPaper>> FetchPapersAsync(
        int? maxResults = null, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var url = ArxivQueryBuilder.BuildSearchUrl(Options.BaseUrl, Options.SearchCategory, maxResults ?? Options.MaxResults, from, to);
        LogFetching(logger, maxResults ?? Options.MaxResults, Options.SearchCategory, from, to);

        var papers = ArxivAtomParser.Parse(await GetFeedAsync(url, cancellationToken), logger);
        LogFetched(logger, papers.Count);
        return papers;
    }

    /// <summary>The latest version of one paper, or null when arXiv doesn't know it.</summary>
    /// <exception cref="ArxivApiException">The API failed, timed out or returned unreadable XML.</exception>
    public async Task<ArxivPaper?> FetchPaperByIdAsync(string arxivId, CancellationToken cancellationToken = default)
    {
        var xml = await GetFeedAsync(ArxivQueryBuilder.BuildIdUrl(Options.BaseUrl, arxivId), cancellationToken);
        var papers = ArxivAtomParser.Parse(xml, logger);
        return papers.Count > 0 ? papers[0] : null;
    }

    /// <summary>Where a paper's PDF is cached: <c>{PdfCacheDir}/{arxivId with '/' → '_'}.pdf</c>.</summary>
    public string PdfPath(string arxivId) =>
        Path.GetFullPath(Path.Combine(Options.PdfCacheDir, arxivId.Replace('/', '_') + ".pdf"));

    /// <summary>
    /// Downloads a paper's PDF into the cache, or returns the cached file unless <paramref name="force"/> is set.
    /// Returns null when the paper has no PDF link. Retries <c>DownloadMaxRetries</c> times in all, waiting
    /// <c>DownloadRetryDelayBaseSeconds × attempt</c> in between; a partial file never stays in the cache (B32).
    /// </summary>
    /// <exception cref="PdfDownloadException">Every attempt failed.</exception>
    public async Task<string?> DownloadPdfAsync(ArxivPaper paper, bool force = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paper);
        if (string.IsNullOrEmpty(paper.PdfUrl))
        {
            LogNoPdfUrl(logger, paper.ArxivId);
            return null;
        }

        var path = PdfPath(paper.ArxivId);
        if (File.Exists(path) && !force)
        {
            LogCached(logger, path);
            return path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".part";
        var attempts = Math.Max(1, Options.DownloadMaxRetries);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(new Uri(paper.PdfUrl), partial, cancellationToken);
                File.Move(partial, path, overwrite: true);
                LogDownloaded(logger, path);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException
                && !cancellationToken.IsCancellationRequested)
            {
                File.Delete(partial);
                if (attempt >= attempts)
                {
                    throw new PdfDownloadException($"PDF download failed after {attempts} attempts: {ex.Message}", ex);
                }

                var delay = TimeSpan.FromSeconds(Options.DownloadRetryDelayBaseSeconds * attempt);
                LogRetrying(logger, paper.ArxivId, attempt, attempts, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, time, cancellationToken);
            }
        }
    }

    private async Task<string> GetFeedAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(new Uri(url), timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArxivApiException($"arXiv API returned error {(int)response.StatusCode}");
            }

            return await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArxivApiException($"arXiv API request timed out after {Options.TimeoutSeconds} s", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ArxivApiException($"Failed to fetch papers from arXiv: {ex.Message}", ex);
        }
    }

    /// <summary>Streams the PDF to <paramref name="path"/>; fails when no data arrives for <c>TimeoutSeconds</c>.</summary>
    private async Task DownloadOnceAsync(Uri url, string path, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(idle.Token);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, idle.Token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                idle.CancelAfter(Timeout);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"PDF download timed out after {Options.TimeoutSeconds} s without data", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetching {MaxResults} {Category} papers from arXiv ({From} to {To})")]
    private static partial void LogFetching(ILogger logger, int maxResults, string category, DateOnly? from, DateOnly? to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fetched {Count} papers")]
    private static partial void LogFetched(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "No PDF URL for paper {ArxivId}")]
    private static partial void LogNoPdfUrl(ILogger logger, string arxivId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Using cached PDF: {Path}")]
    private static partial void LogCached(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Successfully downloaded to {Path}")]
    private static partial void LogDownloaded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "PDF download for {ArxivId} failed (attempt {Attempt}/{Attempts}): {Reason}. Retrying in {DelaySeconds} s")]
    private static partial void LogRetrying(
        ILogger logger, string arxivId, int attempt, int attempts, string reason, double delaySeconds);
}

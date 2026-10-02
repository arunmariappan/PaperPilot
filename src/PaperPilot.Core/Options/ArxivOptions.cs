using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>arXiv API access and PDF download settings.</summary>
public sealed class ArxivOptions
{
    public const string SectionName = "Arxiv";

    [Required, Url]
    public string BaseUrl { get; set; } = "https://export.arxiv.org/api/query";

    [Required]
    public string PdfCacheDir { get; set; } = "./data/arxiv_pdfs";

    /// <summary>Minimum gap between the starts of two arXiv requests (API or PDF), shared by all callers.</summary>
    [Range(0.0, 60.0)]
    public double RateLimitDelaySeconds { get; set; } = 3.0;

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Papers fetched per run. The arXiv API caps a single query at 2000.</summary>
    [Range(1, 2000)]
    public int MaxResults { get; set; } = 15;

    [Required]
    public string SearchCategory { get; set; } = "cs.AI";

    [Range(0, 10)]
    public int DownloadMaxRetries { get; set; } = 3;

    /// <summary>A failed download waits <c>DownloadRetryDelayBaseSeconds × (attempt + 1)</c> before the next try.</summary>
    [Range(0.0, 300.0)]
    public double DownloadRetryDelayBaseSeconds { get; set; } = 5.0;

    [Range(1, 50)]
    public int MaxConcurrentDownloads { get; set; } = 5;

    [Range(1, 16)]
    public int MaxConcurrentParsing { get; set; } = 1;
}

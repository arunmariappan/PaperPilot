namespace PaperPilot.Core.Domain;

/// <summary>Paper metadata as returned by the arXiv API.</summary>
/// <param name="ArxivId">As arXiv reports it, usually with a version (<c>2510.01234v1</c>).</param>
/// <param name="PdfUrl">The <c>application/pdf</c> link from the feed.</param>
public sealed record ArxivPaper(
    string ArxivId,
    string Title,
    IReadOnlyList<string> Authors,
    string Abstract,
    IReadOnlyList<string> Categories,
    DateTimeOffset PublishedDate,
    string PdfUrl);

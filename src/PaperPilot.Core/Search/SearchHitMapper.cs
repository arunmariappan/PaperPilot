using PaperPilot.Core.Contracts;
using PaperPilot.Core.Domain;

namespace PaperPilot.Core.Search;

public static class SearchHitMapper
{
    /// <summary>
    /// Maps an index hit to the API shape. <c>section_name</c> comes from <c>section_title</c> and <c>pdf_url</c> is
    /// derived from the arXiv id; Python returned null for both (B7).
    /// </summary>
    public static SearchHit ToSearchHit(ChunkHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return new SearchHit
        {
            ArxivId = hit.ArxivId,
            Title = hit.Title,
            Authors = hit.Authors,
            Abstract = hit.Abstract,
            PublishedDate = hit.PublishedDate,
            PdfUrl = string.IsNullOrEmpty(hit.ArxivId) ? null : ArxivId.ToPdfUrl(hit.ArxivId),
            Score = hit.Score,
            Highlights = hit.Highlights,
            ChunkText = hit.ChunkText,
            ChunkId = hit.ChunkId,
            SectionName = hit.SectionTitle,
        };
    }
}

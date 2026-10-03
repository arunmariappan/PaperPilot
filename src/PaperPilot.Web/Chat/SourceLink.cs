namespace PaperPilot.Web.Chat;

/// <summary>A source as the UI shows it: <c>arXiv:{id}</c> linking to the abstract page, plus the PDF.</summary>
public sealed record SourceLink(string ArxivId, string AbsUrl, string PdfUrl)
{
    public static SourceLink FromPdfUrl(string pdfUrl)
    {
        var id = Core.Domain.ArxivId.FromPdfUrl(pdfUrl);
        return new SourceLink(id, Core.Domain.ArxivId.ToAbsUrl(id), pdfUrl);
    }
}

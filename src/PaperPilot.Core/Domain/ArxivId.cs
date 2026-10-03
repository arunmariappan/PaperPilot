using System.Text.RegularExpressions;

namespace PaperPilot.Core.Domain;

/// <summary>
/// arXiv identifier helpers. Every arXiv URL in PaperPilot is built here (B8).
/// </summary>
public static partial class ArxivId
{
    private const string PdfUrlPrefix = "https://arxiv.org/pdf/";

    /// <summary>
    /// Removes a trailing version suffix: <c>2510.01234v2</c> → <c>2510.01234</c>,
    /// <c>solv-int/9901001v1</c> → <c>solv-int/9901001</c>. IDs without a version are returned unchanged.
    /// </summary>
    public static string StripVersion(string arxivId)
    {
        ArgumentNullException.ThrowIfNull(arxivId);
        return VersionSuffix().Replace(arxivId, string.Empty);
    }

    /// <summary>The unversioned PDF URL, e.g. <c>https://arxiv.org/pdf/2510.01234.pdf</c>.</summary>
    public static string ToPdfUrl(string arxivId) => $"{PdfUrlPrefix}{StripVersion(arxivId)}.pdf";

    /// <summary>The unversioned abstract page URL, e.g. <c>https://arxiv.org/abs/2510.01234</c>.</summary>
    public static string ToAbsUrl(string arxivId) => $"https://arxiv.org/abs/{StripVersion(arxivId)}";

    /// <summary>
    /// The ID in a source URL from <see cref="ToPdfUrl"/>: <c>https://arxiv.org/pdf/cs/0112017.pdf</c> → <c>cs/0112017</c>.
    /// Any other URL gives its last path segment without <c>.pdf</c>.
    /// </summary>
    public static string FromPdfUrl(string pdfUrl)
    {
        ArgumentNullException.ThrowIfNull(pdfUrl);

        var id = pdfUrl.StartsWith(PdfUrlPrefix, StringComparison.Ordinal)
            ? pdfUrl[PdfUrlPrefix.Length..]
            : pdfUrl[(pdfUrl.LastIndexOf('/') + 1)..];
        return id.EndsWith(".pdf", StringComparison.Ordinal) ? id[..^".pdf".Length] : id;
    }

    [GeneratedRegex(@"v\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffix();
}

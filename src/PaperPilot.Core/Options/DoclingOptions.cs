using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>The docling-serve sidecar that converts PDFs. The AppHost sets <c>BaseUrl</c>.</summary>
public sealed class DoclingOptions
{
    public const string SectionName = "Docling";

    [Required, Url]
    public string BaseUrl { get; set; } = "http://localhost:5011";

    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// docling-serve's PDF text backend. <c>pypdfium2</c> keeps the spaces in headings and text that docling-serve
    /// 1.35's default backend drops, and matches Python Docling 2.52 most closely (measured in phase 4).
    /// </summary>
    [Required]
    public string PdfBackend { get; set; } = "pypdfium2";
}

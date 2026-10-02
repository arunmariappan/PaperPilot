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
}

using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>Limits for PDF parsing. Larger PDFs are stored as metadata only.</summary>
public sealed class PdfParserOptions
{
    public const string SectionName = "PdfParser";

    [Range(1, 1000)]
    public int MaxPages { get; set; } = 30;

    [Range(1, 500)]
    public int MaxFileSizeMb { get; set; } = 20;

    public bool DoOcr { get; set; }

    public bool DoTableStructure { get; set; } = true;
}

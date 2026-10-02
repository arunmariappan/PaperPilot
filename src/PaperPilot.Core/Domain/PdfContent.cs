namespace PaperPilot.Core.Domain;

/// <summary>A section of a parsed paper.</summary>
public sealed record PaperSection(string Title, string Content, int Level = 1);

/// <summary>What a PDF parser extracted from a paper. Metadata about the paper itself comes from arXiv.</summary>
/// <param name="ParserUsed">For example <c>docling</c>.</param>
public sealed record PdfContent(
    IReadOnlyList<PaperSection> Sections,
    string RawText,
    string ParserUsed,
    IReadOnlyDictionary<string, object?> Metadata);

/// <summary>
/// Input for <c>PaperRepository.UpsertAsync</c>: arXiv metadata, plus the parsed PDF when parsing succeeded.
/// </summary>
/// <param name="Content">Null when the PDF was skipped or failed to parse.</param>
public sealed record PaperUpsert(ArxivPaper Metadata, PdfContent? Content);

/// <summary>Counts of papers by PDF processing state.</summary>
/// <param name="ProcessingRate">Processed papers as a percentage of all papers.</param>
/// <param name="TextExtractionRate">Papers with raw text as a percentage of processed papers.</param>
public sealed record PaperProcessingStats(
    int TotalPapers,
    int ProcessedPapers,
    int PapersWithText,
    double ProcessingRate,
    double TextExtractionRate);

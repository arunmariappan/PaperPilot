using System.Text;
using Microsoft.Extensions.Logging;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Text;

namespace PaperPilot.Infrastructure.Pdf;

/// <summary>Sections from Docling's text items, as Python's <c>DoclingParser</c> built them.</summary>
public static class DoclingSectionExtractor
{
    /// <summary>
    /// Walks the items in order. A <c>title</c> or <c>section_header</c> starts a new section named after it; every
    /// other item's text is appended to the current section, which starts as <c>Content</c>. Sections with blank
    /// content are dropped, and content is stripped.
    /// </summary>
    public static IReadOnlyList<PaperSection> Extract(IEnumerable<DoclingText> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var sections = new List<PaperSection>();
        var title = "Content";
        var content = new StringBuilder();

        void Flush()
        {
            var text = PythonText.Strip(content.ToString());
            if (text.Length > 0)
            {
                sections.Add(new PaperSection(title, text));
            }
        }

        foreach (var item in texts)
        {
            if (item.Label is "title" or "section_header")
            {
                Flush();
                title = PythonText.Strip(item.Text ?? string.Empty);
                content.Clear();
            }
            else if (!string.IsNullOrEmpty(item.Text))
            {
                content.Append(item.Text).Append('\n');
            }
        }

        Flush();
        return sections;
    }
}

/// <summary>Validates a PDF, then converts it with docling-serve.</summary>
/// <param name="Content">The parsed content, or null when the PDF was skipped.</param>
/// <param name="SkipReason">Why the PDF was skipped (over the size or page limit).</param>
public sealed record PdfParseResult(PdfContent? Content, string? SkipReason);

public sealed partial class PdfParser(PdfValidator validator, DoclingServeClient docling, ILogger<PdfParser> logger)
{
    public const string ParserName = "docling";

    /// <summary>
    /// Parses a cached PDF. Over the size or page limit, it returns a skip instead (the paper is stored with metadata
    /// only, on purpose).
    /// </summary>
    /// <exception cref="PdfParsingException">The file is not a usable PDF, or the conversion failed.</exception>
    public async Task<PdfParseResult> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var validation = validator.Validate(path);
        switch (validation.Outcome)
        {
            case PdfValidationOutcome.Invalid:
                throw new PdfParsingException(validation.Reason!);
            case PdfValidationOutcome.Skip:
                LogSkipped(logger, validation.Reason!);
                return new PdfParseResult(null, validation.Reason);
        }

        var conversion = await docling.ConvertAsync(path, cancellationToken);
        var content = new PdfContent(
            DoclingSectionExtractor.Extract(conversion.Texts),
            conversion.TextContent,
            ParserName,
            new Dictionary<string, object?>
            {
                ["source"] = ParserName,
                ["note"] = "Content extracted from PDF, metadata comes from arXiv API",
            });
        LogParsed(logger, path, content.Sections.Count, conversion.Status);
        return new PdfParseResult(content, null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipping PDF processing due to size/page limits: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Parsed {Path}: {Sections} sections ({Status})")]
    private static partial void LogParsed(ILogger logger, string path, int sections, string status);
}

using System.Globalization;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using UglyToad.PdfPig;

namespace PaperPilot.Infrastructure.Pdf;

public enum PdfValidationOutcome
{
    /// <summary>Parse it.</summary>
    Valid,

    /// <summary>Within the rules but over a limit: store the paper as metadata only. Not an error.</summary>
    Skip,

    /// <summary>Not a usable PDF: an error.</summary>
    Invalid,
}

public sealed record PdfValidation(PdfValidationOutcome Outcome, string? Reason = null, int Pages = 0);

/// <summary>The checks Python's Docling parser ran before parsing, with PdfPig counting the pages.</summary>
public sealed class PdfValidator(IOptions<PdfParserOptions> options)
{
    public PdfValidation Validate(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return new(PdfValidationOutcome.Invalid, $"PDF file not found: {path}");
        }

        if (file.Length == 0)
        {
            return new(PdfValidationOutcome.Invalid, $"PDF file is empty: {path}");
        }

        var maxBytes = options.Value.MaxFileSizeMb * 1024L * 1024;
        if (file.Length > maxBytes)
        {
            return new(PdfValidationOutcome.Skip, string.Create(CultureInfo.InvariantCulture,
                $"PDF file too large: {file.Length / 1024.0 / 1024:F1}MB > {options.Value.MaxFileSizeMb:F1}MB"));
        }

        if (!HasPdfHeader(path))
        {
            return new(PdfValidationOutcome.Invalid, $"File does not have PDF header: {path}");
        }

        int pages;
        try
        {
            using var document = PdfDocument.Open(path);
            pages = document.NumberOfPages;
        }
        catch (Exception ex)
        {
            // PdfPig throws many exception types for damaged files; any of them means "invalid".
            return new(PdfValidationOutcome.Invalid, $"Error validating PDF {path}: {ex.Message}");
        }

        return pages > options.Value.MaxPages
            ? new(PdfValidationOutcome.Skip, $"PDF has too many pages: {pages} > {options.Value.MaxPages}", pages)
            : new(PdfValidationOutcome.Valid, Pages: pages);
    }

    private static bool HasPdfHeader(string path)
    {
        Span<byte> header = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header.SequenceEqual("%PDF-"u8);
    }
}

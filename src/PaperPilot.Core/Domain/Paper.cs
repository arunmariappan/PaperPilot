using System.Text.Json;

namespace PaperPilot.Core.Domain;

/// <summary>An entity whose <c>CreatedAt</c> and <c>UpdatedAt</c> are set when it is saved.</summary>
public interface ITimestamped
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A stored paper: arXiv metadata, plus the parsed PDF content once parsing has succeeded.</summary>
public sealed class Paper : ITimestamped
{
    public Guid Id { get; set; }

    public required string ArxivId { get; set; }

    public required string Title { get; set; }

    public List<string> Authors { get; set; } = [];

    public required string Abstract { get; set; }

    public List<string> Categories { get; set; } = [];

    public DateTimeOffset PublishedDate { get; set; }

    public required string PdfUrl { get; set; }

    public string? RawText { get; set; }

    public List<PaperSection>? Sections { get; set; }

    public List<string>? References { get; set; }

    public string? ParserUsed { get; set; }

    public JsonDocument? ParserMetadata { get; set; }

    public bool PdfProcessed { get; set; }

    public DateTimeOffset? PdfProcessingDate { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

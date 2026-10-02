namespace PaperPilot.Core.Domain;

/// <summary>One run of the ingestion job (N2): what it targeted, what it did and how it ended.</summary>
public sealed class IngestionRun
{
    public Guid Id { get; set; }

    /// <summary>First submission date fetched (UTC).</summary>
    public DateOnly TargetFrom { get; set; }

    /// <summary>Last submission date fetched (UTC).</summary>
    public DateOnly TargetTo { get; set; }

    /// <summary><see cref="IngestionTriggers.Scheduled"/> or <see cref="IngestionTriggers.Manual"/>.</summary>
    public required string Trigger { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>One of <see cref="IngestionRunStatus"/>.</summary>
    public required string Status { get; set; }

    public int PapersFetched { get; set; }

    public int PdfsDownloaded { get; set; }

    public int PdfsParsed { get; set; }

    /// <summary>PDFs over the size or page limit, stored as metadata only.</summary>
    public int PdfsSkipped { get; set; }

    public int PapersStored { get; set; }

    public int ChunksCreated { get; set; }

    public int ChunksIndexed { get; set; }

    public int EmbeddingsGenerated { get; set; }

    public List<string> Errors { get; set; } = [];

    /// <summary>Documents in the chunk index after the run.</summary>
    public long? IndexDocCountAfter { get; set; }

    public string? HangfireJobId { get; set; }
}

public static class IngestionRunStatus
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public static class IngestionTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

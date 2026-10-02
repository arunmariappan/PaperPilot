using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>Word-based chunking. All sizes are in words.</summary>
public sealed class ChunkingOptions : IValidatableObject
{
    public const string SectionName = "Chunking";

    [Range(1, 10_000)]
    public int ChunkSize { get; set; } = 600;

    [Range(0, 10_000)]
    public int OverlapSize { get; set; } = 100;

    [Range(1, 10_000)]
    public int MinChunkSize { get; set; } = 100;

    /// <summary>Chunk by paper section when sections are available, falling back to word windows.</summary>
    public bool SectionBased { get; set; } = true;

    /// <summary>Sections shorter than this are combined with their neighbours.</summary>
    [Range(1, 10_000)]
    public int SectionMinWords { get; set; } = 100;

    /// <summary>Sections longer than this are split into word windows.</summary>
    [Range(1, 10_000)]
    public int SectionMaxWords { get; set; } = 800;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (OverlapSize >= ChunkSize)
        {
            yield return new ValidationResult(
                "Overlap size must be less than chunk size", [nameof(OverlapSize), nameof(ChunkSize)]);
        }

        if (SectionMinWords >= SectionMaxWords)
        {
            yield return new ValidationResult(
                "Section minimum words must be less than section maximum words",
                [nameof(SectionMinWords), nameof(SectionMaxWords)]);
        }
    }
}

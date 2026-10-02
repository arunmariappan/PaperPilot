using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>Service identity reported by <c>/api/v1/health</c>.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    [Required]
    public string Version { get; set; } = "0.1.0";

    [Required]
    public string ServiceName { get; set; } = "rag-api";
}

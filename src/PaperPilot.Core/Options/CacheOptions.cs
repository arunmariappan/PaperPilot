using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>The Redis answer cache. The connection comes from the <c>redis</c> connection string.</summary>
public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    [Range(1, 24 * 30)]
    public int TtlHours { get; set; } = 6;
}

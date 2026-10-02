using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>
/// Langfuse tracing and feedback scores. Off unless the AppHost runs the Langfuse stack, which then sets every value here.
/// </summary>
public sealed class LangfuseOptions
{
    public const string SectionName = "Langfuse";

    public bool Enabled { get; set; }

    [Required, Url]
    public string BaseUrl { get; set; } = "http://localhost:3010";

    public string PublicKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;
}

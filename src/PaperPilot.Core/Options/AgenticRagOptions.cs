using System.ComponentModel.DataAnnotations;

namespace PaperPilot.Core.Options;

/// <summary>
/// The agentic RAG workflow behind <c>/ask-agentic</c>. The defaults are Python's <c>GraphConfig</c> and the
/// temperatures its nodes used.
/// </summary>
public sealed class AgenticRagOptions
{
    public const string SectionName = "Agentic";

    /// <summary>Retrievals before the agent gives up. The query is rewritten before each retry.</summary>
    [Range(1, 5)]
    public int MaxRetrievalAttempts { get; set; } = 2;

    /// <summary>Questions whose guardrail score (0–100) is at least this are in scope.</summary>
    [Range(0, 100)]
    public int GuardrailThreshold { get; set; } = 60;

    [Range(0.0, 2.0)]
    public float GuardrailTemperature { get; set; }

    [Range(0.0, 2.0)]
    public float GradingTemperature { get; set; }

    [Range(0.0, 2.0)]
    public float RewriteTemperature { get; set; } = 0.3f;

    [Range(0.0, 2.0)]
    public float GenerateTemperature { get; set; }
}

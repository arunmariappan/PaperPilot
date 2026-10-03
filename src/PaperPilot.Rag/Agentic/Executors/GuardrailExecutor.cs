using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag.Agentic.Executors;

/// <summary>
/// Scores whether the question is about CS/AI/ML research (0–100). If the LLM fails or returns an invalid score,
/// the score is 50, which is below the default threshold, so the question is treated as out of scope.
/// </summary>
internal sealed partial class GuardrailExecutor(
    IChatClient chat, ChatOptionsFactory chatOptions, IOptions<AgenticRagOptions> options, ILogger<GuardrailExecutor> logger)
    : AgentNode("guardrail")
{
    internal const int FallbackScore = 50;

    public override async ValueTask<AgentRunState> HandleAsync(
        AgentRunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var threshold = options.Value.GuardrailThreshold;

        using var span = RagTelemetry.Source.StartActivity("guardrail_validation");
        span.SetInput(new { Query = message.OriginalQuestion, Threshold = threshold });
        span.SetMetadata(("node", "guardrail"), ("model", message.Model));

        GuardrailScoring scoring;
        try
        {
            var response = await chat.GetResponseAsync<GuardrailScoring>(
                AgentPrompts.Guardrail(message.OriginalQuestion),
                AgentJson.Options,
                chatOptions.Create(message.Model, options.Value.GuardrailTemperature),
                cancellationToken: cancellationToken);
            scoring = response.Result;
            if (scoring.Score is < 0 or > 100)
            {
                throw new InvalidOperationException($"Score {scoring.Score} is outside 0-100.");
            }

            var decision = scoring.Score >= threshold ? "continue" : "out_of_scope";
            span.SetOutput(new { scoring.Score, scoring.Reason, Decision = decision });
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogGuardrailFailed(logger, ex);
            scoring = new GuardrailScoring(FallbackScore, $"LLM validation failed, using conservative default: {ex.Message}");
            span.Degrade(ex, ObservationLevels.Warning);
            span.SetOutput(new { scoring.Score, scoring.Reason, Error = ex.Message });
        }

        return message with { Guardrail = scoring };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM guardrail validation failed, falling back to the default score")]
    private static partial void LogGuardrailFailed(ILogger logger, Exception exception);
}

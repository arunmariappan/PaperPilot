using System.Diagnostics;
using System.Text.Json;
using PaperPilot.Core.Contracts;

namespace PaperPilot.Rag.Telemetry;

/// <summary>
/// Spans for the RAG pipeline, named as in Python (<c>rag_request</c>, <c>cache_lookup</c>, <c>query_embedding</c>,
/// <c>search_retrieval</c>, <c>prompt_construction</c>), with the attributes Langfuse maps to trace and observation
/// fields. The LLM call's span comes from the chat client (<c>PaperPilot.Llm</c>).
/// </summary>
public static class RagTelemetry
{
    public const string SourceName = "PaperPilot.Rag";

    /// <summary>The Langfuse user id Python used for API requests.</summary>
    public const string ApiUser = "api_user";

    internal static ActivitySource Source { get; } = new(SourceName);

    /// <summary>
    /// Starts the root span of a RAG request and sets the Langfuse trace fields: name, user, session and input.
    /// </summary>
    internal static Activity? StartRequest(string name, string query, string userId = ApiUser)
    {
        var activity = Source.StartActivity(name);
        activity?.SetTag(LangfuseAttributes.TraceName, name);
        activity?.SetTag(LangfuseAttributes.UserId, userId);
        activity?.SetTag(LangfuseAttributes.SessionId, $"session_{userId}");
        activity?.SetJsonTag(LangfuseAttributes.TraceInput, new { Query = query });
        activity?.SetInput(new { Query = query });
        return activity;
    }

    /// <summary>Sets the request's output on the root span and the trace, as Python's <c>end_request</c> did.</summary>
    internal static void EndRequest(this Activity? activity, string answer, TimeSpan duration)
    {
        if (activity is null)
        {
            return;
        }

        var output = new { Answer = answer, TotalDurationSeconds = Math.Round(duration.TotalSeconds, 3), ResponseLength = answer.Length };
        activity.SetOutput(output);
        activity.SetJsonTag(LangfuseAttributes.TraceOutput, output);
    }

    /// <summary>Marks the span as failed and records the exception.</summary>
    internal static void Fail(this Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
    }

    internal static void SetInput(this Activity? activity, object payload) =>
        activity?.SetJsonTag(LangfuseAttributes.ObservationInput, payload);

    internal static void SetOutput(this Activity? activity, object payload) =>
        activity?.SetJsonTag(LangfuseAttributes.ObservationOutput, payload);

    /// <summary>A JSON attribute, with snake_case names like the payloads Python logged.</summary>
    private static void SetJsonTag(this Activity activity, string name, object payload)
    {
        if (activity.IsAllDataRequested)
        {
            activity.SetTag(name, JsonSerializer.Serialize(payload, ApiJson.Options));
        }
    }
}

/// <summary>Span attributes that Langfuse's OpenTelemetry ingestion maps to trace and observation fields.</summary>
public static class LangfuseAttributes
{
    public const string TraceName = "langfuse.trace.name";
    public const string UserId = "langfuse.user.id";
    public const string SessionId = "langfuse.session.id";
    public const string TraceInput = "langfuse.trace.input";
    public const string TraceOutput = "langfuse.trace.output";
    public const string ObservationInput = "langfuse.observation.input";
    public const string ObservationOutput = "langfuse.observation.output";
}

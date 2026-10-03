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
    public static Activity? StartRequest(string name, string query, string userId = ApiUser)
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
    internal static void EndRequest(this Activity? activity, string answer, TimeSpan duration) =>
        activity.SetTraceOutput(new { Answer = answer, TotalDurationSeconds = Math.Round(duration.TotalSeconds, 3), ResponseLength = answer.Length });

    /// <summary>Sets an output on the span and on its trace.</summary>
    public static void SetTraceOutput(this Activity? activity, object payload)
    {
        activity.SetOutput(payload);
        activity?.SetJsonTag(LangfuseAttributes.TraceOutput, payload);
    }

    /// <summary>Langfuse metadata on the span, one attribute per key.</summary>
    internal static void SetMetadata(this Activity? activity, params ReadOnlySpan<(string Key, object? Value)> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            activity?.SetTag(LangfuseAttributes.ObservationMetadataPrefix + key, value);
        }
    }

    /// <summary>Langfuse metadata on the span's trace, one attribute per key.</summary>
    internal static void SetTraceMetadata(this Activity? activity, params ReadOnlySpan<(string Key, object? Value)> metadata)
    {
        foreach (var (key, value) in metadata)
        {
            activity?.SetTag(LangfuseAttributes.TraceMetadataPrefix + key, value);
        }
    }

    /// <summary>
    /// Records a failure the code recovered from: the exception as a span event and a Langfuse level
    /// (<c>WARNING</c> or <c>ERROR</c>). Only <c>ERROR</c> also marks the span as failed.
    /// </summary>
    internal static void Degrade(this Activity? activity, Exception exception, string level)
    {
        if (activity is null)
        {
            return;
        }

        activity.AddException(exception);
        activity.SetTag(LangfuseAttributes.ObservationLevel, level);
        activity.SetTag(LangfuseAttributes.ObservationStatusMessage, exception.Message);
        if (level == ObservationLevels.Error)
        {
            activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        }
    }

    /// <summary>Shortens <paramref name="text"/> for a span payload as Python did: the first characters plus "...".</summary>
    internal static string Preview(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        var end = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end] + "...";
    }

    /// <summary>Marks the span as failed and records the exception.</summary>
    public static void Fail(this Activity? activity, Exception exception)
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
    public const string ObservationLevel = "langfuse.observation.level";
    public const string ObservationStatusMessage = "langfuse.observation.status_message";
    public const string ObservationMetadataPrefix = "langfuse.observation.metadata.";
    public const string TraceMetadataPrefix = "langfuse.trace.metadata.";
}

/// <summary>Values of <see cref="LangfuseAttributes.ObservationLevel"/>.</summary>
public static class ObservationLevels
{
    public const string Warning = "WARNING";
    public const string Error = "ERROR";
}

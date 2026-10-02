using System.Text.Json.Serialization;

namespace PaperPilot.Core.Contracts;

/// <summary>
/// One server-sent event from <c>/stream</c>; only the properties of its kind are written. The order is
/// <see cref="Metadata"/>, then <see cref="Token"/>s, then <see cref="Completed"/>, or a single <see cref="NoResults"/>,
/// or <see cref="Failed"/> at any point.
/// </summary>
public sealed record RagStreamEvent
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Answer { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Sources { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunksUsed { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SearchMode { get; init; }

    /// <summary>The next piece of the answer.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Chunk { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Done { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    /// <summary>The answer Python's <c>/stream</c> sends when no chunks match.</summary>
    public const string NoResultsAnswer = "No relevant information found.";

    /// <summary><c>{sources, chunks_used, search_mode}</c>, sent before the first token.</summary>
    public static RagStreamEvent Metadata(IReadOnlyList<string> sources, int chunksUsed, string searchMode) =>
        new() { Sources = sources, ChunksUsed = chunksUsed, SearchMode = searchMode };

    /// <summary><c>{chunk}</c></summary>
    public static RagStreamEvent Token(string chunk) => new() { Chunk = chunk };

    /// <summary><c>{answer, done: true}</c>, the last event, with the whole answer.</summary>
    public static RagStreamEvent Completed(string answer) => new() { Answer = answer, Done = true };

    /// <summary><c>{answer, sources: [], done: true}</c>, the only event when no chunks match.</summary>
    public static RagStreamEvent NoResults() => new() { Answer = NoResultsAnswer, Sources = [], Done = true };

    /// <summary><c>{error}</c>, the last event when the request fails.</summary>
    public static RagStreamEvent Failed(string error) => new() { Error = error };
}

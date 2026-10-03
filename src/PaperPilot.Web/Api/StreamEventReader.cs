using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PaperPilot.Core.Contracts;

namespace PaperPilot.Web.Api;

/// <summary>Reads a <c>/stream</c> response body: server-sent events whose <c>data</c> is a snake_case JSON object.</summary>
internal static class StreamEventReader
{
    public static async IAsyncEnumerable<StreamEvent> ReadAsync(
        Stream body, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parser = SseParser.Create(body, static (_, data) => Deserialize(data));
        await foreach (var item in parser.EnumerateAsync(cancellationToken))
        {
            if (ToEvent(item.Data) is { } streamEvent)
            {
                yield return streamEvent;
            }
        }
    }

    /// <summary>The kind is told by which properties are set; a <c>done</c> event can also carry <c>sources</c>.</summary>
    internal static StreamEvent? ToEvent(RagStreamEvent? data) => data switch
    {
        { Error: { } error } => new StreamError(error),
        { Done: true } => new StreamDone(data.Answer ?? string.Empty, data.Sources),
        { Chunk: { } chunk } => new StreamChunk(chunk),
        { Sources: { } sources } => new StreamMetadata(sources, data.ChunksUsed ?? 0, data.SearchMode ?? "unknown"),
        _ => null,
    };

    private static RagStreamEvent? Deserialize(ReadOnlySpan<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize<RagStreamEvent>(data, ApiJson.Options);
        }
        catch (JsonException)
        {
            // Malformed events are skipped, as the Gradio client did.
            return null;
        }
    }
}

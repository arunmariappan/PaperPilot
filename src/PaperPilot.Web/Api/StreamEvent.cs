namespace PaperPilot.Web.Api;

/// <summary>One event from <c>/stream</c>, by kind (see <see cref="Core.Contracts.RagStreamEvent"/>).</summary>
public abstract record StreamEvent;

/// <summary>Sent before the first token: what the answer draws on.</summary>
public sealed record StreamMetadata(IReadOnlyList<string> Sources, int ChunksUsed, string SearchMode) : StreamEvent;

/// <summary>The next piece of the answer.</summary>
public sealed record StreamChunk(string Text) : StreamEvent;

/// <summary>The last event, with the whole answer. <see cref="Sources"/> is set (empty) only when no chunks matched.</summary>
public sealed record StreamDone(string Answer, IReadOnlyList<string>? Sources) : StreamEvent;

/// <summary>The request failed. Nothing follows it.</summary>
public sealed record StreamError(string Message) : StreamEvent;

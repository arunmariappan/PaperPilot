using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Search;
using PaperPilot.Rag.Retrieval;

namespace PaperPilot.UnitTests.Rag;

/// <summary>Answers with <see cref="Tokens"/> (joined for non-streaming calls) and records every call.</summary>
internal sealed class FakeChatClient : IChatClient
{
    public IReadOnlyList<string> Tokens { get; set; } = ["Transformers ", "use attention [arXiv:2610.00001]."];

    /// <summary>Thrown instead of answering; for streaming, after <see cref="Tokens"/>.</summary>
    public Exception? Failure { get; set; }

    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));
        return Failure is null
            ? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(Tokens))))
            : Task.FromException<ChatResponse>(Failure);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));
        foreach (var token in Tokens)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, token);
        }

        if (Failure is not null)
        {
            throw Failure;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class FakeRetriever : IPaperRetriever
{
    public RetrievalResult Result { get; set; } = Retrieved(
        Chunk("2610.00001v1", 0, "Transformers use self-attention."),
        Chunk("2610.00001v1", 1, "Positional encodings."),
        Chunk("2610.00002v2", 0, "Reward models."));

    public Exception? Failure { get; set; }

    public int Calls => Requests.Count;

    /// <summary>Every call's arguments, in order.</summary>
    public List<(string Query, int TopK, bool UseHybrid, IReadOnlyList<string>? Categories)> Requests { get; } = [];

    public Task<RetrievalResult> RetrieveAsync(
        string query, int topK, bool useHybrid, IReadOnlyList<string>? categories, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add((query, topK, useHybrid, categories));
        }

        return Failure is null ? Task.FromResult(Result) : Task.FromException<RetrievalResult>(Failure);
    }

    public static RetrievalResult Retrieved(params ChunkHit[] chunks) => Retrieved(SearchModes.Hybrid, chunks);

    public static RetrievalResult Retrieved(string mode, params ChunkHit[] chunks) => new(
        chunks,
        [.. chunks.Select(c => ArxivId.ToPdfUrl(c.ArxivId)).Distinct()],
        [.. chunks.Select(c => c.ArxivId)],
        chunks.Length,
        mode);

    public static ChunkHit Chunk(string arxivId, int index, string text) =>
        new($"{arxivId}:{index}", arxivId, "Title", null, null, null, text, index, null, 1.0, null);
}

internal sealed class InMemoryAnswerCache : IAnswerCache
{
    public Dictionary<string, AskResponse> Entries { get; } = [];

    public Task<AskResponse?> TryGetAsync(AskRequest request, string model, CancellationToken cancellationToken = default) =>
        Task.FromResult(Entries.GetValueOrDefault(CacheKey.Compute(request, model)));

    public Task StoreAsync(AskRequest request, string model, AskResponse response, CancellationToken cancellationToken = default)
    {
        Entries[CacheKey.Compute(request, model)] = response;
        return Task.CompletedTask;
    }
}

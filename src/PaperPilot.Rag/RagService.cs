using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag.Prompts;
using PaperPilot.Rag.Retrieval;
using PaperPilot.Rag.Telemetry;

namespace PaperPilot.Rag;

/// <summary>
/// Classic RAG for <c>/ask</c> and <c>/stream</c>: cache lookup → retrieval → prompt → generation → cache store.
/// A cache failure never fails a request. A search outage does (B6): <see cref="AskAsync(AskRequest, CancellationToken)"/> throws and
/// <see cref="StreamAsync"/> ends with an <c>{error}</c> event.
/// </summary>
public sealed partial class RagService(
    IPaperRetriever retriever,
    IChatClient chat,
    ChatOptionsFactory chatOptions,
    IAnswerCache cache,
    TimeProvider time,
    ILogger<RagService> logger)
{
    /// <summary>The answer <c>/ask</c> gives when no chunks match. It isn't cached.</summary>
    public const string NoResultsAnswer = "I couldn't find any relevant information in the papers to answer your question.";

    internal const float Temperature = 0.7f;
    internal const float TopP = 0.9f;

    /// <summary>Length of the <c>chunk</c> events that replay a cached answer.</summary>
    internal const int ReplaySliceLength = 40;

    private const string RequestSpanName = "rag_request";

    public Task<AskResponse> AskAsync(AskRequest request, CancellationToken cancellationToken = default) =>
        AskAsync(request, RagTelemetry.ApiUser, cancellationToken);

    /// <param name="request">The question and retrieval settings.</param>
    /// <param name="userId">The user the trace is recorded for, e.g. <c>telegram:{chatId}</c>.</param>
    /// <param name="cancellationToken">Stops retrieval and generation.</param>
    public async Task<AskResponse> AskAsync(AskRequest request, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = chatOptions.ResolveModel(request.Model);
        using var activity = RagTelemetry.StartRequest(RequestSpanName, request.Query, userId);
        var started = time.GetTimestamp();

        try
        {
            if (await LookupAsync(request, model, cancellationToken) is { } cached)
            {
                activity.EndRequest(cached.Answer, time.GetElapsedTime(started));
                return cached;
            }

            var retrieval = await RetrieveAsync(request, cancellationToken);
            if (retrieval.Chunks.Count == 0)
            {
                activity.EndRequest(NoResultsAnswer, time.GetElapsedTime(started));
                return Response(request, NoResultsAnswer, retrieval with { Sources = [] });
            }

            var prompt = BuildPrompt(request.Query, retrieval.Chunks);
            var completion = await chat.GetResponseAsync(
                prompt.ToMessages(), chatOptions.Create(model, Temperature, TopP), cancellationToken);

            var response = Response(request, completion.Text, retrieval);
            activity.EndRequest(response.Answer, time.GetElapsedTime(started));
            await StoreAsync(request, model, response, cancellationToken);
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Includes timeouts, which are OperationCanceledExceptions too.
            activity.Fail(ex);
            throw;
        }
    }

    /// <summary>
    /// <c>{sources, chunks_used, search_mode}</c>, then the answer as <c>{chunk}</c> events, then <c>{answer, done}</c>.
    /// A cached answer is replayed in <see cref="ReplaySliceLength"/>-character slices that keep its whitespace (B17).
    /// Failures end the stream with <c>{error}</c> instead of throwing.
    /// </summary>
    public async IAsyncEnumerable<RagStreamEvent> StreamAsync(
        AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The pipeline runs as an ordinary async method that writes to a channel. Inside an async iterator,
        // Activity.Current would be lost at every yield, and later spans would lose their parent.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = Channel.CreateUnbounded<RagStreamEvent>(new() { SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(request, events.Writer, stop.Token);

        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            // Stops generation when the caller stops reading, e.g. when the client disconnects.
            await stop.CancelAsync();
            await producer;
        }
    }

    private async Task ProduceAsync(AskRequest request, ChannelWriter<RagStreamEvent> events, CancellationToken cancellationToken)
    {
        var model = chatOptions.ResolveModel(request.Model);
        using var activity = RagTelemetry.StartRequest(RequestSpanName, request.Query);
        var started = time.GetTimestamp();

        try
        {
            if (await LookupAsync(request, model, cancellationToken) is { } cached)
            {
                events.TryWrite(RagStreamEvent.Metadata(cached.Sources, cached.ChunksUsed, cached.SearchMode));
                foreach (var slice in Slices(cached.Answer, ReplaySliceLength))
                {
                    events.TryWrite(RagStreamEvent.Token(slice));
                }

                events.TryWrite(RagStreamEvent.Completed(cached.Answer));
                activity.EndRequest(cached.Answer, time.GetElapsedTime(started));
                return;
            }

            var retrieval = await RetrieveAsync(request, cancellationToken);
            if (retrieval.Chunks.Count == 0)
            {
                events.TryWrite(RagStreamEvent.NoResults());
                activity.EndRequest(RagStreamEvent.NoResultsAnswer, time.GetElapsedTime(started));
                return;
            }

            events.TryWrite(RagStreamEvent.Metadata(retrieval.Sources, retrieval.Chunks.Count, retrieval.SearchMode));

            var prompt = BuildPrompt(request.Query, retrieval.Chunks);
            var answer = new StringBuilder();
            await foreach (var update in chat.GetStreamingResponseAsync(
                prompt.ToMessages(), chatOptions.Create(model, Temperature, TopP), cancellationToken))
            {
                if (update.Text is { Length: > 0 } text)
                {
                    answer.Append(text);
                    events.TryWrite(RagStreamEvent.Token(text));
                }
            }

            var response = Response(request, answer.ToString(), retrieval);
            events.TryWrite(RagStreamEvent.Completed(response.Answer));
            activity.EndRequest(response.Answer, time.GetElapsedTime(started));
            await StoreAsync(request, model, response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped reading; nobody is left to tell.
        }
        catch (Exception ex)
        {
            // Any failure becomes the stream's {error} event, as in Python.
            LogStreamFailed(logger, ex);
            activity.Fail(ex);
            events.TryWrite(RagStreamEvent.Failed(ex.Message));
        }
        finally
        {
            events.TryComplete();
        }
    }

    /// <summary>Splits <paramref name="text"/> into pieces of <paramref name="length"/> characters, never inside a surrogate pair.</summary>
    internal static IEnumerable<string> Slices(string text, int length)
    {
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(start + length, text.Length);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
            {
                end++;
            }

            yield return text[start..end];
            start = end;
        }
    }

    private async Task<AskResponse?> LookupAsync(AskRequest request, string model, CancellationToken cancellationToken)
    {
        using var span = RagTelemetry.Source.StartActivity("cache_lookup");
        span.SetInput(new { Key = CacheKey.Compute(request, model) });

        var cached = await cache.TryGetAsync(request, model, cancellationToken);

        span?.SetTag("cache.hit", cached is not null);
        span.SetOutput(new { Hit = cached is not null });
        if (cached is not null)
        {
            LogCacheHit(logger);
        }

        return cached;
    }

    private Task<RetrievalResult> RetrieveAsync(AskRequest request, CancellationToken cancellationToken) =>
        retriever.RetrieveAsync(request.Query, request.TopK, request.UseHybrid, request.Categories, cancellationToken);

    private static RagPrompt BuildPrompt(string query, IReadOnlyList<ChunkHit> chunks)
    {
        using var span = RagTelemetry.Source.StartActivity("prompt_construction");
        span.SetInput(new { ChunkCount = chunks.Count });

        var prompt = RagPromptBuilder.Build(query, chunks);

        var combined = prompt.Combined;
        span.SetOutput(new
        {
            PromptLength = combined.Length,
            PromptPreview = combined.Length > 200 ? combined[..200] + "..." : combined,
        });
        return prompt;
    }

    /// <summary>Caches an answer unless it's blank: a blank answer means generation went wrong.</summary>
    private Task StoreAsync(AskRequest request, string model, AskResponse response, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(response.Answer)
            ? Task.CompletedTask
            : cache.StoreAsync(request, model, response, cancellationToken);

    private static AskResponse Response(AskRequest request, string answer, RetrievalResult retrieval) => new()
    {
        Query = request.Query,
        Answer = answer,
        Sources = retrieval.Sources,
        ChunksUsed = retrieval.Chunks.Count,
        SearchMode = retrieval.SearchMode,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Returning cached response for exact query match")]
    private static partial void LogCacheHit(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Streaming error")]
    private static partial void LogStreamFailed(ILogger logger, Exception exception);
}

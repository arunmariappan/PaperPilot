using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag;
using PaperPilot.Rag.Telemetry;
using static PaperPilot.UnitTests.Rag.FakeRetriever;

namespace PaperPilot.UnitTests.Rag;

public sealed class RagServiceTests : IDisposable
{
    private readonly FakeChatClient _chat = new();
    private readonly FakeRetriever _retriever = new();
    private readonly InMemoryAnswerCache _cache = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AskRequest Question => new() { Query = "What are transformers?" };

    [Fact]
    public async Task Ask_answers_from_the_retrieved_chunks_and_caches_the_answer()
    {
        var response = await Service().AskAsync(Question, Ct);

        response.Answer.ShouldBe("Transformers use attention [arXiv:2610.00001].");
        response.Sources.ShouldBe(["https://arxiv.org/pdf/2610.00001.pdf", "https://arxiv.org/pdf/2610.00002.pdf"]);
        (response.Query, response.ChunksUsed, response.SearchMode).ShouldBe(("What are transformers?", 3, "hybrid"));
        _cache.Entries[CacheKey.Compute(Question, "qwen3.5:9b")].ShouldBe(response);

        var (messages, options) = _chat.Calls.ShouldHaveSingleItem();
        messages.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User]);
        messages[1].Text.ShouldContain("[3. arXiv:2610.00002v2]\nReward models.");
        (options!.ModelId, options.Temperature, options.TopP).ShouldBe(("qwen3.5:9b", 0.7f, 0.9f));
        options.AdditionalProperties!["think"].ShouldBe(false);
    }

    [Fact]
    public async Task Ask_uses_the_requested_model_and_caches_per_model()
    {
        var request = Question with { Model = "llama3.2:1b" };

        await Service().AskAsync(request, Ct);

        _chat.Calls.ShouldHaveSingleItem().Options!.ModelId.ShouldBe("llama3.2:1b");
        _cache.Entries.Keys.ShouldBe([CacheKey.Compute(request, "llama3.2:1b")]);
    }

    [Fact]
    public async Task Ask_without_matching_chunks_gives_the_canned_answer_and_does_not_cache_it()
    {
        _retriever.Result = Retrieved(SearchModes.Bm25);

        var response = await Service().AskAsync(Question, Ct);

        response.Answer.ShouldBe("I couldn't find any relevant information in the papers to answer your question.");
        (response.Sources.Count, response.ChunksUsed, response.SearchMode).ShouldBe((0, 0, "bm25"));
        _chat.Calls.ShouldBeEmpty();
        _cache.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cache_hit_skips_retrieval_and_generation()
    {
        var cached = new AskResponse { Query = Question.Query, Answer = "Cached.", Sources = [], ChunksUsed = 1, SearchMode = "bm25" };
        await _cache.StoreAsync(Question, "qwen3.5:9b", cached, Ct);

        (await Service().AskAsync(Question, Ct)).ShouldBe(cached);

        (_retriever.Calls, _chat.Calls.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Search_mode_is_the_mode_retrieval_actually_used() // B24
    {
        _retriever.Result = Retrieved(SearchModes.Bm25, Chunk("2610.00001v1", 0, "Text."));

        var response = await Service().AskAsync(Question with { UseHybrid = true }, Ct);

        response.SearchMode.ShouldBe("bm25");
    }

    [Fact]
    public async Task A_search_outage_fails_ask_instead_of_answering_from_nothing() // B6
    {
        _retriever.Failure = new SearchUnavailableException("OpenSearch is unavailable");

        await Should.ThrowAsync<SearchUnavailableException>(() => Service().AskAsync(Question, Ct));

        _chat.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_blank_answer_is_not_cached()
    {
        _chat.Tokens = ["  "];

        await Service().AskAsync(Question, Ct);

        _cache.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_sends_metadata_then_tokens_then_the_whole_answer_and_caches_it()
    {
        var events = await Stream();

        events[0].ShouldBe(RagStreamEvent.Metadata(events[0].Sources!, 3, "hybrid"));
        events[0].Sources!.ShouldBe(["https://arxiv.org/pdf/2610.00001.pdf", "https://arxiv.org/pdf/2610.00002.pdf"]);
        events.Skip(1).SkipLast(1).Select(e => e.Chunk).ShouldBe(["Transformers ", "use attention [arXiv:2610.00001]."]);
        events[^1].ShouldBe(RagStreamEvent.Completed("Transformers use attention [arXiv:2610.00001]."));
        _cache.Entries.ShouldHaveSingleItem().Value.Answer.ShouldBe("Transformers use attention [arXiv:2610.00001].");
    }

    [Fact]
    public async Task Stream_replays_a_cached_answer_in_slices_that_keep_its_whitespace() // B17
    {
        var answer = "First paragraph, long enough to need two slices.\n\n- a bullet\n- another bullet\n\nLast line.";
        await _cache.StoreAsync(Question, "qwen3.5:9b",
            new AskResponse { Query = Question.Query, Answer = answer, Sources = ["s"], ChunksUsed = 2, SearchMode = "bm25" }, Ct);

        var events = await Stream();

        events[0].ShouldBe(RagStreamEvent.Metadata(events[0].Sources!, 2, "bm25"));
        var slices = events.Skip(1).SkipLast(1).Select(e => e.Chunk!).ToList();
        string.Concat(slices).ShouldBe(answer);
        slices.ShouldAllBe(s => s.Length <= 40);
        events[^1].ShouldBe(RagStreamEvent.Completed(answer));
        (_retriever.Calls, _chat.Calls.Count).ShouldBe((0, 0));
    }

    [Fact]
    public async Task Stream_without_matching_chunks_sends_one_no_results_event()
    {
        _retriever.Result = Retrieved();

        var events = await Stream();

        events.ShouldHaveSingleItem().ShouldBe(RagStreamEvent.NoResults() with { Sources = events[0].Sources });
        events[0].Sources.ShouldBeEmpty();
        _cache.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_ends_with_an_error_event_when_search_is_down() // B6
    {
        _retriever.Failure = new SearchUnavailableException("OpenSearch is unavailable");

        var events = await Stream();

        events.ShouldHaveSingleItem().Error.ShouldBe("OpenSearch is unavailable");
    }

    [Fact]
    public async Task Stream_ends_with_an_error_event_when_generation_fails_midway()
    {
        _chat.Failure = new HttpRequestException("Ollama went away");

        var events = await Stream();

        events.Select(e => (e.ChunksUsed is not null, e.Chunk, e.Error)).ShouldBe([
            (true, null, null),
            (false, "Transformers ", null),
            (false, "use attention [arXiv:2610.00001].", null),
            (false, null, "Ollama went away"),
        ]);
        _cache.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stream_spans_hang_under_rag_request()
    {
        using var test = new ActivitySource("PaperPilot.Tests");
        var stopped = new ConcurrentQueue<Activity>(); // other test classes may emit PaperPilot.Llm spans in parallel
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is RagTelemetry.SourceName or "PaperPilot.Tests" or LlmRegistration.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        var chat = new ChatClientBuilder(_chat).UseOpenTelemetry(sourceName: LlmRegistration.ActivitySourceName).Build();

        ActivityTraceId traceId;
        using (var root = test.StartActivity("test"))
        {
            traceId = root!.TraceId;
            await Stream(Service(chat));
        }

        var spans = stopped.Where(s => s.TraceId == traceId && s.OperationName != "test").ToList();
        var request = spans.Single(s => s.OperationName == "rag_request");
        spans.Where(s => s != request)
            .Select(s => (s.Source.Name == LlmRegistration.ActivitySourceName ? "chat" : s.OperationName, s.ParentSpanId))
            .ShouldBe([("cache_lookup", request.SpanId), ("prompt_construction", request.SpanId), ("chat", request.SpanId)],
                ignoreOrder: true);
        request.GetTagItem(LangfuseAttributes.TraceOutput).ShouldBeOfType<string>().ShouldContain("\"response_length\":");
    }

    [Theory]
    [InlineData("", 3, new string[0])]
    [InlineData("abcdefg", 3, new[] { "abc", "def", "g" })]
    [InlineData("ab😀cd", 3, new[] { "ab😀", "cd" })] // the emoji's two UTF-16 halves stay together
    public void Slices_cover_the_text_without_splitting_surrogate_pairs(string text, int length, string[] expected) =>
        RagService.Slices(text, length).ShouldBe(expected);

    public void Dispose() => _chat.Dispose();

    private RagService Service(IChatClient? chat = null) => new(
        _retriever,
        chat ?? _chat,
        new ChatOptionsFactory(Microsoft.Extensions.Options.Options.Create(new OllamaOptions())),
        _cache,
        TimeProvider.System,
        NullLogger<RagService>.Instance);

    private async Task<List<RagStreamEvent>> Stream(RagService? service = null)
    {
        var events = new List<RagStreamEvent>();
        await foreach (var item in (service ?? Service()).StreamAsync(Question, Ct))
        {
            events.Add(item);
        }

        return events;
    }
}

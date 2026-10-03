using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PaperPilot.Api.Telegram;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Telemetry;
using PaperPilot.UnitTests.Rag;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static PaperPilot.UnitTests.Rag.FakeRetriever;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PaperPilot.UnitTests.Telegram;

public sealed class TelegramUpdateHandlerTests : IDisposable
{
    private const long ChatId = 42;

    private readonly FakeTelegramSender _sender = new();
    private readonly FakeChatClient _chat = new();
    private readonly FakeRetriever _retriever = new();
    private readonly FakeAgent _agent = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_and_help_reply_with_pythons_text()
    {
        await Handle("/start");
        await Handle("/help");

        _sender.Texts.ShouldBe(
        [
            "Welcome to arXiv Paper Curator!\n\nAsk me questions about CS papers and I'll provide answers with sources.\n\n"
            + "Commands:\n/help - Show this help\n/search <keywords> - Search papers",
            "Send me any question about computer science research papers.\n\nExamples:\n- What are transformer architectures?\n"
            + "- How does BERT work?\n- Explain attention mechanisms\n\nUse /search to find specific papers.",
        ]);
    }

    [Theory]
    [InlineData("/HELP", true)]
    [InlineData("/help@PaperPilotBot", true)]
    [InlineData("/help@paperpilotbot", true)]
    [InlineData("/help@SomeOtherBot", false)]
    [InlineData("/unknown", false)]
    public async Task Commands_ignore_case_and_must_be_addressed_to_this_bot(string command, bool answered)
    {
        await Handle(command);

        _sender.Texts.Count.ShouldBe(answered ? 1 : 0);
        (_retriever.Calls, _chat.Calls.Count).ShouldBe((0, 0)); // a command is never treated as a question
    }

    [Fact]
    public async Task Search_without_keywords_shows_the_usage()
    {
        await Handle("/search");

        _sender.Texts.ShouldBe(["Usage: /search <keywords>\nExample: /search neural networks"]);
        _retriever.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Search_lists_papers_from_a_ten_chunk_hybrid_search()
    {
        await Handle("/search   neural \t networks");

        _retriever.Requests.ShouldHaveSingleItem().ShouldBe(("neural networks", 10, true, null));
        _sender.Sent.ShouldHaveSingleItem().ShouldBe((ChatId,
            "Found 2 papers:\n\n1. Title\nhttps://arxiv.org/abs/2610.00001\n\n2. Title\nhttps://arxiv.org/abs/2610.00002\n\n", false));
        _sender.Typing.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Theory]
    [InlineData("unavailable", "Search is temporarily unavailable. Please try again later.")] // B6
    [InlineData("other", "Search failed: parse_exception")]
    [InlineData("none", "No papers found. Try different keywords.")]
    public async Task Search_failures_and_empty_results_get_a_message(string outcome, string reply)
    {
        _retriever.Failure = outcome switch
        {
            "unavailable" => new SearchUnavailableException("OpenSearch is down"),
            "other" => new SearchQueryException("parse_exception"),
            _ => null,
        };
        _retriever.Result = Retrieved();

        await Handle("/search transformers");

        _sender.Texts.ShouldBe([reply]);
    }

    [Fact]
    public async Task A_question_is_answered_from_the_shared_rag_service_with_sources() // C12
    {
        await Handle("What are transformers?");

        _retriever.Requests.ShouldHaveSingleItem().ShouldBe(("What are transformers?", 3, true, null));
        _sender.Sent.ShouldHaveSingleItem().ShouldBe((ChatId,
            "*Answer:*\nTransformers use attention [arXiv:2610.00001].\n\n*Sources:*\n"
            + "1. https://arxiv.org/abs/2610.00001\n2. https://arxiv.org/abs/2610.00002\n", true));
    }

    [Fact]
    public async Task A_question_without_matching_chunks_gets_pythons_message()
    {
        _retriever.Result = Retrieved();

        await Handle("What is the meaning of life?");

        _sender.Texts.ShouldBe(["No relevant papers found. Try rephrasing your question."]);
    }

    [Fact]
    public async Task Markdown_that_telegram_rejects_is_resent_as_plain_text()
    {
        _sender.RejectMarkdown = true;

        await Handle("What are transformers?");

        var (_, text, markdown) = _sender.Sent.ShouldHaveSingleItem();
        (text.StartsWith("*Answer:*\n", StringComparison.Ordinal), markdown).ShouldBe((true, false));
    }

    [Fact]
    public async Task A_long_answer_arrives_in_several_messages() // B19
    {
        _chat.Tokens = [string.Join("\n\n", Enumerable.Repeat(new string('x', 1999), 5))];

        await Handle("Explain everything about transformers.");

        _sender.Sent.Count.ShouldBe(3);
        _sender.Sent.ShouldAllBe(m => m.Text.Length <= 4096 && m.Markdown);
        _sender.Sent[0].Text.ShouldStartWith("*Answer:*\n");
        _sender.Sent[^1].Text.ShouldEndWith("*Sources:*\n1. https://arxiv.org/abs/2610.00001\n2. https://arxiv.org/abs/2610.00002\n");
    }

    [Theory]
    [InlineData("search", "Search is temporarily unavailable. Please try again later.")] // B6
    [InlineData("llm", "Error: Ollama is down")]
    public async Task A_failed_question_gets_an_error_message(string failure, string reply)
    {
        if (failure == "search")
        {
            _retriever.Failure = new SearchUnavailableException("OpenSearch is down");
        }
        else
        {
            _chat.Failure = new HttpRequestException("Ollama is down");
        }

        await Handle("What are transformers?");

        _sender.Texts.ShouldBe([reply]);
    }

    [Fact]
    public async Task Questions_are_traced_as_the_telegram_chat()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RagTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        await Handle("What are transformers? (traced)");
        await Handle("/search traced keywords");

        stopped.Where(a => a.GetTagItem(LangfuseAttributes.UserId) is "telegram:42")
            .Select(a => a.OperationName).ShouldBe(["rag_request", "telegram_search"], ignoreOrder: true);
    }

    [Fact]
    public async Task Agent_answers_with_reasoning_steps() // N5
    {
        await Handle("/agent");
        await Handle("/agent What are transformer architectures?");

        _sender.Sent[0].Text.ShouldBe("Usage: /agent <question>\nExample: /agent What are transformer architectures?");
        _agent.Requests.ShouldHaveSingleItem().ShouldBe(("What are transformer architectures?", "telegram:42"));
        _sender.Sent[1].Markdown.ShouldBeTrue();
        _sender.Sent[1].Text.ShouldEndWith("*Reasoning:*\n- Validated query scope (score: 90/100)\n- Generated answer from context\n");
    }

    [Fact]
    public async Task Typing_is_shown_until_the_answer_is_ready()
    {
        var time = new FakeTimeProvider();
        var answer = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowChat = new SlowChatClient(answer.Task);

        var handling = Handle("What are transformers?", Handler(slowChat, time));
        await WaitUntil(() => _sender.Typing == 1);
        time.Advance(TelegramUpdateHandler.TypingInterval);
        await WaitUntil(() => _sender.Typing == 2);
        time.Advance(TelegramUpdateHandler.TypingInterval);
        await WaitUntil(() => _sender.Typing == 3);
        answer.SetResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done.")));
        await handling;
        time.Advance(TelegramUpdateHandler.TypingInterval * 3);

        (_sender.Typing, _sender.Sent.Count).ShouldBe((3, 1));
    }

    [Fact]
    public async Task Messages_without_text_are_ignored()
    {
        await Handler().HandleAsync(new Message { Chat = new Chat { Id = ChatId } }, "PaperPilotBot", Ct);

        _sender.Sent.ShouldBeEmpty();
    }

    public void Dispose() => _chat.Dispose();

    private Task Handle(string text, TelegramUpdateHandler? handler = null) =>
        (handler ?? Handler()).HandleAsync(Message(text), "PaperPilotBot", Ct);

    private TelegramUpdateHandler Handler(IChatClient? chat = null, TimeProvider? time = null) => new(
        _sender,
        new RagService(
            _retriever,
            chat ?? _chat,
            new ChatOptionsFactory(MsOptions.Create(new OllamaOptions())),
            new InMemoryAnswerCache(),
            TimeProvider.System,
            NullLogger<RagService>.Instance),
        _retriever,
        _agent,
        time ?? TimeProvider.System,
        NullLogger<TelegramUpdateHandler>.Instance);

    /// <summary>A text message; Telegram marks a leading <c>/command</c> with a bot-command entity.</summary>
    private static Message Message(string text)
    {
        var command = text.StartsWith('/') ? text.Split(' ', '\t')[0] : null;
        return new Message
        {
            Chat = new Chat { Id = ChatId, Type = ChatType.Private },
            Text = text,
            Entities = command is null ? null : [new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = command.Length }],
        };
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeTelegramSender : ITelegramSender
    {
        private int _typing;

        public List<(long ChatId, string Text, bool Markdown)> Sent { get; } = [];

        public List<string> Texts => [.. Sent.Select(m => m.Text)];

        public int Typing => Volatile.Read(ref _typing);

        /// <summary>Fails every Markdown message the way Telegram does when it can't parse the entities.</summary>
        public bool RejectMarkdown { get; set; }

        public Task SendTextAsync(long chatId, string text, bool markdown, CancellationToken cancellationToken)
        {
            if (markdown && RejectMarkdown)
            {
                throw new ApiRequestException("Bad Request: can't parse entities", 400);
            }

            Sent.Add((chatId, text, markdown));
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(long chatId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _typing);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgent : IAgenticRagService
    {
        public List<(string Query, string UserId)> Requests { get; } = [];

        public Task<AgenticAskResponse> AskAsync(AskRequest request, string userId, CancellationToken cancellationToken = default)
        {
            Requests.Add((request.Query, userId));
            return Task.FromResult(new AgenticAskResponse
            {
                Query = request.Query,
                Answer = "Transformers use self-attention.",
                Sources = ["https://arxiv.org/pdf/2610.00001.pdf"],
                ChunksUsed = 3,
                SearchMode = "hybrid",
                ReasoningSteps = ["Validated query scope (score: 90/100)", "Generated answer from context"],
                RetrievalAttempts = 1,
            });
        }
    }

    private sealed class SlowChatClient(Task<ChatResponse> answer) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => answer;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

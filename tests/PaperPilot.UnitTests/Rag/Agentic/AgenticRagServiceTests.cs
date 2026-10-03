using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Llm;
using PaperPilot.Rag;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Agentic.Executors;
using PaperPilot.Rag.Retrieval;
using PaperPilot.Rag.Telemetry;
using static PaperPilot.UnitTests.Rag.FakeRetriever;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace PaperPilot.UnitTests.Rag.Agentic;

/// <summary>The whole workflow with a scripted LLM and a fake retriever.</summary>
public sealed class AgenticRagServiceTests : IDisposable
{
    private const string Question = "What are transformers?";
    private const string Irrelevant = """{"binary_score": "no", "reasoning": "Off topic."}""";
    private const string Relevant = """{"binary_score": "yes", "reasoning": "About attention."}""";

    private readonly ScriptedChatClient _chat = new();
    private readonly FakeRetriever _retriever = new();
    private readonly List<ServiceProvider> _providers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Relevant_documents_on_the_first_try_are_answered_with_their_sources()
    {
        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(ScriptedChatClient.Answer);
        response.Sources.ShouldBe(["https://arxiv.org/pdf/2610.00001.pdf", "https://arxiv.org/pdf/2610.00002.pdf"]); // B1
        (response.Query, response.ChunksUsed, response.SearchMode, response.RetrievalAttempts).ShouldBe((Question, 3, "hybrid", 1));
        response.ReasoningSteps.ShouldBe(
        [
            "Validated query scope (score: 90/100)",
            "Retrieved documents (1 attempt(s))",
            "Graded documents (1 relevant)",
            "Generated answer from context",
        ]);
        _retriever.Requests.ShouldBe([(Question, 3, true, null)]);
        _chat.Calls.Select(c => (c.Call, c.Options!.ModelId, c.Options.Temperature)).ShouldBe(
        [
            (AgentCall.Guardrail, "qwen3.5:9b", 0f),
            (AgentCall.Grade, "qwen3.5:9b", 0f),
            (AgentCall.Generate, "qwen3.5:9b", 0f),
        ]);
        _chat.Calls.ShouldAllBe(c => Equals(c.Options!.AdditionalProperties!["think"], false));
        _chat.Prompts(AgentCall.Generate).Single().ShouldContain(
            "Retrieved Research Papers:\n[1] arXiv:2610.00001v1 — Title\nTransformers use self-attention.\n\n"
            + "[2] arXiv:2610.00001v1 — Title\nPositional encodings.\n\n[3] arXiv:2610.00002v2 — Title\nReward models.\n\n"
            + "User Question: What are transformers?\n"); // B4
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    public async Task Questions_scored_below_the_threshold_are_declined_without_retrieval(int score, bool inScope)
    {
        _chat.Script(AgentCall.Guardrail, $$"""{"score": {{score}}, "reason": "Scored."}""");

        var response = await Service().AskAsync(Ask("What is 2+2?"), Ct);

        _retriever.Calls.ShouldBe(inScope ? 1 : 0);
        if (!inScope)
        {
            response.Answer.ShouldStartWith("I apologize, but I can only help with questions about academic research papers");
            response.Answer.ShouldContain("Your question: 'What is 2+2?'");
            (response.RetrievalAttempts, response.ChunksUsed, response.SearchMode, response.Sources.Count).ShouldBe((0, 0, "hybrid", 0));
            response.ReasoningSteps.ShouldBe([$"Validated query scope (score: {score}/100)", "Responded as out of scope"]); // B28
            _chat.Calls.Select(c => c.Call).ShouldBe([AgentCall.Guardrail]);
        }
    }

    [Fact]
    public async Task Irrelevant_documents_twice_stop_after_one_rewrite() // B5
    {
        _chat.Script(AgentCall.Grade, Irrelevant);

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(AgentMessages.MaxAttempts(2));
        _chat.Calls.Select(c => c.Call).ShouldBe([AgentCall.Guardrail, AgentCall.Grade, AgentCall.Rewrite, AgentCall.Grade]);
        (response.RetrievalAttempts, response.Sources.Count, response.ChunksUsed).ShouldBe((2, 0, 3));
        response.ReasoningSteps.ShouldBe(
        [
            "Validated query scope (score: 90/100)",
            "Retrieved documents (2 attempt(s))",
            "Graded documents (0 relevant)",
            "Rewritten query for better results",
            "Stopped after 2 retrieval attempts",
        ]);
    }

    [Fact]
    public async Task A_rewrite_is_used_for_retrieval_but_the_answer_is_to_the_original_question() // B27
    {
        _chat.Script(AgentCall.Grade, Irrelevant, Relevant);

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(ScriptedChatClient.Answer);
        _retriever.Requests.Select(r => r.Query).ShouldBe([Question, "transformer self-attention architecture"]);
        _chat.Prompts(AgentCall.Rewrite).ShouldHaveSingleItem().ShouldContain("Here is the initial question:\nWhat are transformers?\n");
        _chat.Prompts(AgentCall.Grade).ShouldAllBe(p => p.Contains("User Question: What are transformers?\n"));
        _chat.Prompts(AgentCall.Generate).ShouldHaveSingleItem().ShouldContain("User Question: What are transformers?\n");
        _chat.Calls.Single(c => c.Call == AgentCall.Rewrite).Options!.Temperature.ShouldBe(0.3f);
        response.Sources.Count.ShouldBe(2);
        response.ReasoningSteps.ShouldBe(
        [
            "Validated query scope (score: 90/100)",
            "Retrieved documents (2 attempt(s))",
            "Graded documents (1 relevant)",
            "Rewritten query for better results",
            "Generated answer from context",
        ]);
    }

    [Fact]
    public async Task No_matching_papers_means_no_grading_call_one_rewrite_and_the_max_attempts_answer()
    {
        _retriever.Result = Retrieved();

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(AgentMessages.MaxAttempts(2));
        _chat.Calls.Select(c => c.Call).ShouldBe([AgentCall.Guardrail, AgentCall.Rewrite]);
        response.ReasoningSteps.ShouldBe(
        [
            "Validated query scope (score: 90/100)",
            "Retrieved documents (2 attempt(s))",
            "Rewritten query for better results",
            "Stopped after 2 retrieval attempts",
        ]);
    }

    [Fact]
    public async Task More_attempts_can_be_configured()
    {
        _chat.Script(AgentCall.Grade, Irrelevant);

        var response = await Service(new AgenticRagOptions { MaxRetrievalAttempts = 3 }).AskAsync(Ask(Question), Ct);

        (response.RetrievalAttempts, _chat.Prompts(AgentCall.Rewrite).Count()).ShouldBe((3, 2));
        response.Answer.ShouldStartWith("I apologize, but I couldn't find relevant research papers after 3 attempts.");
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("not json")]
    [InlineData("""{"score": 150, "reason": "Too keen."}""")]
    [InlineData("""{"reason": "No score."}""")]
    [InlineData("""{"score": 90, "reason": null}""")]
    public async Task A_failed_guardrail_scores_50_which_is_out_of_scope(string reply)
    {
        _chat.Script(AgentCall.Guardrail, reply == "exception" ? new HttpRequestException("Ollama is down") : reply);

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.ReasoningSteps.ShouldBe(["Validated query scope (score: 50/100)", "Responded as out of scope"]);
        _retriever.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("Transformers use self-attention.", true)]
    [InlineData("Hi.", false)] // "[1] arXiv:2610.00001v1 — Title\nHi." is 35 characters
    public async Task A_failed_grading_falls_back_to_the_context_length(string chunkText, bool relevant)
    {
        _retriever.Result = Retrieved(Chunk("2610.00001v1", 0, chunkText));
        _chat.Script(AgentCall.Grade, "maybe?");

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.ReasoningSteps[^1].ShouldBe(relevant ? "Generated answer from context" : "Stopped after 2 retrieval attempts");
        response.ReasoningSteps.ShouldContain(relevant ? "Graded documents (1 relevant)" : "Graded documents (0 relevant)");
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("""{"rewritten_query": "   ", "reasoning": "Blank."}""")]
    public async Task A_failed_rewrite_appends_keywords_to_the_question(string reply)
    {
        _chat.Script(AgentCall.Grade, Irrelevant, Relevant)
            .Script(AgentCall.Rewrite, reply == "exception" ? new TaskCanceledException("Timed out") : reply);

        var response = await Service().AskAsync(Ask(Question), Ct);

        _retriever.Requests.Select(r => r.Query).ShouldBe([Question, $"{Question} research paper arxiv machine learning"]);
        response.Answer.ShouldBe(ScriptedChatClient.Answer);
    }

    [Fact]
    public async Task A_failed_generation_answers_with_the_error()
    {
        _chat.Script(AgentCall.Generate, new HttpRequestException("Ollama is down"));

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(AgentMessages.GenerationFailed("Ollama is down"));
        response.ReasoningSteps[^1].ShouldBe("Generated answer from context");
    }

    [Fact]
    public async Task A_search_outage_ends_with_an_explicit_answer() // B6
    {
        _retriever.Failure = new SearchUnavailableException("OpenSearch is unavailable");

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.Answer.ShouldBe(AgentMessages.SearchUnavailable);
        (response.Sources.Count, response.ChunksUsed, response.SearchMode, response.RetrievalAttempts).ShouldBe((0, 0, "hybrid", 1));
        response.ReasoningSteps.ShouldBe(
            ["Validated query scope (score: 90/100)", "Retrieved documents (1 attempt(s))", "Search was unavailable"]);
        _chat.Calls.Select(c => c.Call).ShouldBe([AgentCall.Guardrail]);
    }

    [Fact]
    public async Task A_rejected_search_query_fails_the_request()
    {
        _retriever.Failure = new SearchQueryException("parse_exception");

        await Should.ThrowAsync<SearchQueryException>(() => Service().AskAsync(Ask(Question), Ct));
    }

    [Fact]
    public async Task Request_options_reach_the_retriever_and_the_llm() // B2
    {
        _retriever.Result = Retrieved(SearchModes.Bm25, Chunk("2610.00001v1", 0, "Transformers use self-attention."));

        var response = await Service().AskAsync(
            Ask(Question) with { TopK = 5, UseHybrid = true, Model = "llama3.2:1b", Categories = ["cs.CL"] }, Ct);

        var request = _retriever.Requests.ShouldHaveSingleItem();
        (request.TopK, request.UseHybrid).ShouldBe((5, true));
        request.Categories.ShouldBe(["cs.CL"]);
        _chat.Calls.ShouldAllBe(c => c.Options!.ModelId == "llama3.2:1b");
        (response.ChunksUsed, response.SearchMode).ShouldBe((1, "bm25")); // B3: the mode retrieval actually used
    }

    [Fact]
    public async Task Node_spans_hang_under_agentic_rag_request_whose_trace_id_is_returned()
    {
        var stopped = new ConcurrentQueue<Activity>(); // other tests emit PaperPilot.Rag spans in parallel
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RagTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        _chat.Script(AgentCall.Grade, Irrelevant, Relevant);

        var response = await Service().AskAsync(Ask(Question), Ct);

        response.TraceId.ShouldNotBeNull().ShouldMatch("^[0-9a-f]{32}$");
        var spans = stopped.Where(s => s.TraceId.ToHexString() == response.TraceId).ToList();
        var root = spans.Single(s => s.OperationName == "agentic_rag_request");
        spans.Where(s => s != root).ShouldAllBe(s => s.ParentSpanId == root.SpanId);
        spans.Where(s => s != root).Select(s => s.OperationName).ShouldBe(
        [
            "guardrail_validation",
            "document_retrieval_initiation",
            "document_grading",
            "query_rewriting",
            "document_retrieval_initiation",
            "document_grading",
            "answer_generation",
        ]);
        root.GetTagItem(LangfuseAttributes.TraceName).ShouldBe("agentic_rag_request");
        root.GetTagItem(LangfuseAttributes.UserId).ShouldBe("api_user");
        (root.GetTagItem("langfuse.trace.metadata.env"), root.GetTagItem("langfuse.trace.metadata.service"),
            root.GetTagItem("langfuse.trace.metadata.top_k"), root.GetTagItem("langfuse.trace.metadata.model"))
            .ShouldBe(("testing", "agentic_rag", 3, "qwen3.5:9b"));
        root.GetTagItem(LangfuseAttributes.TraceOutput).ShouldBeOfType<string>().ShouldContain("\"retrieval_attempts\":2");
        spans.Where(s => s.OperationName == "document_grading")
            .Select(s => (string)s.GetTagItem(LangfuseAttributes.ObservationOutput)!)
            .Select(o => o[..o.IndexOf(',', StringComparison.Ordinal)])
            .ShouldBe(["{\"routing_decision\":\"rewrite_query\"", "{\"routing_decision\":\"generate_answer\""]);
    }

    [Fact]
    public async Task One_workflow_serves_concurrent_requests()
    {
        var service = Service();
        var questions = Enumerable.Range(1, 8).Select(i => $"Question {i} about transformers?").ToList();

        var responses = await Task.WhenAll(questions.Select(q => service.AskAsync(Ask(q), Ct)));

        responses.Select(r => r.Query).ShouldBe(questions);
        responses.ShouldAllBe(r => r.Answer == ScriptedChatClient.Answer && r.RetrievalAttempts == 1);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_instead_of_triggering_a_fallback()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var hanging = new HangingChatClient(cancel);

        await Should.ThrowAsync<OperationCanceledException>(() => Service(chat: hanging).AskAsync(Ask(Question), cancel.Token));

        _retriever.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_question_is_rejected(string query) =>
        await Should.ThrowAsync<ArgumentException>(() => Service().AskAsync(Ask(query), Ct));

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _chat.Dispose();
    }

    private static AskRequest Ask(string query) => new() { Query = query };

    private IAgenticRagService Service(AgenticRagOptions? options = null, IChatClient? chat = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(chat ?? _chat)
            .AddSingleton<IPaperRetriever>(_retriever)
            .AddSingleton(new ChatOptionsFactory(MsOptions.Create(new OllamaOptions())))
            .AddSingleton(MsOptions.Create(options ?? new AgenticRagOptions()))
            .AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Testing" })
            .AddSingleton(TimeProvider.System)
            .AddAgenticRag();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        _providers.Add(provider);
        return provider.GetRequiredService<IAgenticRagService>();
    }

    /// <summary>Waits until the request is cancelled, then cancels it once the first LLM call has started.</summary>
    private sealed class HangingChatClient(CancellationTokenSource cancel) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

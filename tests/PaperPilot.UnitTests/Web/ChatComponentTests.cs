using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaperPilot.Core.Contracts;
using PaperPilot.Web.Api;
using PaperPilot.Web.Chat;
using PaperPilot.Web.Components.Chat;
using PaperPilot.Web.Components.Pages;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Web;

/// <summary>bUnit tests of the chat components, with the API played by WireMock.</summary>
public sealed class ChatComponentTests : BunitContext
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private static readonly string[] InstalledModels = ["qwen3.5:9b"];

    private readonly WireMockServer _api = WireMockServer.Start();
    private readonly HttpClient _http;

    public ChatComponentTests()
    {
        _http = new HttpClient { BaseAddress = new Uri($"{_api.Url}/") };
        Services.AddSingleton(new PaperPilotApiClient(_http));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
    }

    [Fact]
    public void An_answer_containing_markup_renders_it_as_text()
    {
        var turn = new ChatTurn("q", ChatMode.Classic);
        turn.Complete("Hi <script>alert(1)</script> <img src=x onerror=alert(1)>");

        var cut = Render<AnswerView>(p => p.Add(x => x.Turn, turn));

        cut.FindAll("script").ShouldBeEmpty();
        cut.FindAll("img").ShouldBeEmpty();
        cut.Find(".markdown").TextContent.ShouldContain("<script>alert(1)</script>");
    }

    [Fact]
    public void Sources_link_to_the_abstract_page_and_the_pdf()
    {
        var turn = new ChatTurn("q", ChatMode.Classic);
        turn.Complete("A.", ["https://arxiv.org/pdf/cs/0112017.pdf"]);

        var links = Render<AnswerView>(p => p.Add(x => x.Turn, turn)).FindAll(".sources a");

        links.Select(a => (a.TextContent, a.GetAttribute("href"))).ShouldBe(
        [
            ("arXiv:cs/0112017", "https://arxiv.org/abs/cs/0112017"),
            ("PDF", "https://arxiv.org/pdf/cs/0112017.pdf"),
        ]);
    }

    [Fact]
    public void Feedback_is_offered_only_with_a_trace_id()
    {
        Render<FeedbackBar>(p => p.Add(x => x.Turn, Agentic(traceId: null))).Markup.Trim().ShouldBeEmpty();
        Render<FeedbackBar>(p => p.Add(x => x.Turn, Agentic(TraceId)).Add(x => x.Available, false)).Markup.Trim().ShouldBeEmpty();

        Render<FeedbackBar>(p => p.Add(x => x.Turn, Agentic(TraceId))).FindAll("button").Count.ShouldBe(2);
    }

    [Fact]
    public async Task Thumbs_up_sends_a_score_of_one_and_thanks_the_user()
    {
        _api.Given(Request.Create().WithPath("/api/v1/feedback"))
            .RespondWith(Response.Create().WithBodyAsJson(new { success = true, message = "Feedback recorded successfully" }));
        var turn = Agentic(TraceId);
        var cut = Render<FeedbackBar>(p => p.Add(x => x.Turn, turn));

        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "  Useful  " });
        await cut.Find("button[aria-label='Helpful']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Thanks, your feedback (👍) was recorded."));
        turn.Feedback.ShouldBe(1);
        var body = JsonNode.Parse(_api.LogEntries.ShouldHaveSingleItem().RequestMessage!.Body!)!;
        (body["trace_id"]!.GetValue<string>(), body["score"]!.GetValue<double>(), body["comment"]!.GetValue<string>())
            .ShouldBe((TraceId, 1, "Useful"));
    }

    [Fact]
    public async Task Feedback_reports_when_langfuse_is_off()
    {
        _api.Given(Request.Create().WithPath("/api/v1/feedback"))
            .RespondWith(Response.Create().WithStatusCode(503).WithBodyAsJson(new { detail = "Langfuse tracing is disabled." }));
        var unavailable = false;
        var cut = Render<FeedbackBar>(p => p.Add(x => x.Turn, Agentic(TraceId)).Add(x => x.OnUnavailable, () => unavailable = true));

        await cut.Find("button[aria-label='Not helpful']").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => unavailable.ShouldBeTrue());
    }

    [Fact]
    public void The_page_streams_an_answer_with_its_sources()
    {
        _api.Given(Request.Create().WithPath("/api/v1/stream").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "text/event-stream")
                .WithBody(StreamEventReaderTests.Fixture("stream-answer.sse")));
        var cut = RenderChat();

        cut.Find("#question").Input("What is BERT?");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".chat > .answer .status").TextContent.ShouldStartWith("Answered"));
        cut.Find(".chat > .answer .markdown").TextContent.ShouldContain("distilling knowledge from BERT");
        cut.Find(".chat > .answer .sources a").TextContent.ShouldBe("arXiv:2610.00820");
        cut.Find(".chat > .answer .status").TextContent.ShouldContain("hybrid search · 1 chunks");
    }

    [Fact]
    public async Task The_page_asks_for_a_question_and_fills_the_form_from_an_example()
    {
        var cut = RenderChat();

        await cut.Find("form").SubmitAsync();
        cut.WaitForAssertion(() => cut.Find(".form-message").TextContent.ShouldBe("Please enter a question."));
        _api.LogEntries.ShouldNotContain(e => e.RequestMessage!.Path == "/api/v1/stream");

        await cut.FindAll(".examples button")[1].ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("#question").GetAttribute("value").ShouldBe("How do convolutional neural networks work?"));
        cut.Find("#top-k").GetAttribute("value").ShouldBe("5");
        cut.Find("#categories").GetAttribute("value").ShouldBe("cs.CV, cs.LG");
        cut.Find("#model").GetAttribute("value").ShouldBe("qwen3.5:9b");
        cut.FindAll(".form-message").ShouldBeEmpty();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http.Dispose();
            _api.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>The page, once <c>/models</c> has filled the model dropdown.</summary>
    private IRenderedComponent<Chat> RenderChat()
    {
        StubModels();
        var cut = Render<Chat>();
        cut.WaitForAssertion(() => cut.Find("#model").GetAttribute("value").ShouldBe("qwen3.5:9b"));
        return cut;
    }

    private void StubModels() =>
        _api.Given(Request.Create().WithPath("/api/v1/models"))
            .RespondWith(Response.Create().WithBodyAsJson(new { default_model = "qwen3.5:9b", models = InstalledModels }));

    private static ChatTurn Agentic(string? traceId)
    {
        var turn = new ChatTurn("q", ChatMode.Agentic);
        turn.Complete(new AgenticAskResponse
        {
            Query = "q",
            Answer = "A.",
            Sources = [],
            ChunksUsed = 0,
            SearchMode = "hybrid",
            ReasoningSteps = [],
            RetrievalAttempts = 1,
            TraceId = traceId,
        });
        return turn;
    }
}

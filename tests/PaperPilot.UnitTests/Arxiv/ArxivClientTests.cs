using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Arxiv;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Arxiv;

public sealed class ArxivClientTests : IDisposable
{
    private static readonly byte[] Pdf = "%PDF-1.7 fake pdf bytes"u8.ToArray();

    private readonly WireMockServer _arxiv = WireMockServer.Start();
    private readonly string _cacheDir = Directory.CreateTempSubdirectory("paperpilot-pdfs-").FullName;
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Papers_are_fetched_with_the_python_query()
    {
        _arxiv.Given(Request.Create().WithPath("/api/query").UsingGet()).RespondWith(Response.Create()
            .WithBody(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "arxiv", "feed-cs-ai-20260930.xml"))));

        var papers = await Client().FetchPapersAsync(3, new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30), Ct);

        papers.Count.ShouldBe(3);
        _arxiv.LogEntries.ShouldHaveSingleItem().RequestMessage!.RawQuery.ShouldBe(
            "?search_query=cat:cs.AI%20AND%20submittedDate:[202609300000+TO+202609302359]"
            + "&start=0&max_results=3&sortBy=submittedDate&sortOrder=descending");
    }

    [Theory]
    [InlineData(503, "arXiv API returned error 503")]
    [InlineData(200, "Failed to parse arXiv XML response")]
    public async Task Api_failures_are_reported(int status, string message)
    {
        _arxiv.Given(Request.Create().WithPath("/api/query")).RespondWith(Response.Create().WithStatusCode(status).WithBody("<oops"));

        var error = await Should.ThrowAsync<ArxivApiException>(() => Client().FetchPapersAsync(cancellationToken: Ct));

        error.Message.ShouldStartWith(message);
    }

    [Fact]
    public async Task A_pdf_is_downloaded_once_and_then_served_from_the_cache()
    {
        _arxiv.Given(Request.Create().WithPath("/pdf/2610.00001v1")).RespondWith(Response.Create().WithBody(Pdf));
        var client = Client();

        var path = await client.DownloadPdfAsync(Paper("2610.00001v1"), cancellationToken: Ct);
        var again = await client.DownloadPdfAsync(Paper("2610.00001v1"), cancellationToken: Ct);

        (path, again).ShouldBe((Path.Combine(_cacheDir, "2610.00001v1.pdf"), Path.Combine(_cacheDir, "2610.00001v1.pdf")));
        (await File.ReadAllBytesAsync(path!, Ct)).ShouldBe(Pdf);
        _arxiv.LogEntries.Count.ShouldBe(1);

        await client.DownloadPdfAsync(Paper("2610.00001v1"), force: true, Ct);
        _arxiv.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_failed_download_is_retried()
    {
        _arxiv.Given(Request.Create().WithPath("/pdf/2610.00001v1")).InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(502));
        _arxiv.Given(Request.Create().WithPath("/pdf/2610.00001v1")).InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithBody(Pdf));

        var path = await Client().DownloadPdfAsync(Paper("2610.00001v1"), cancellationToken: Ct);

        (await File.ReadAllBytesAsync(path!, Ct)).ShouldBe(Pdf);
        _arxiv.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task After_the_last_attempt_nothing_is_left_in_the_cache() // B32
    {
        _arxiv.Given(Request.Create().WithPath("/pdf/2610.00001v1")).RespondWith(Response.Create().WithStatusCode(500));

        var error = await Should.ThrowAsync<PdfDownloadException>(() => Client().DownloadPdfAsync(Paper("2610.00001v1"), cancellationToken: Ct));

        error.Message.ShouldStartWith("PDF download failed after 3 attempts");
        _arxiv.LogEntries.Count.ShouldBe(3);
        Directory.GetFiles(_cacheDir).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_paper_without_a_pdf_link_has_no_pdf() =>
        (await Client().DownloadPdfAsync(Paper("2610.00001v1") with { PdfUrl = "" }, cancellationToken: Ct)).ShouldBeNull();

    [Fact]
    public async Task Requests_start_at_least_the_rate_limit_apart() // B26
    {
        var clock = new FakeTimeProvider();
        using var limiter = new ArxivRateLimiter(Microsoft.Extensions.Options.Options.Create(new ArxivOptions { RateLimitDelaySeconds = 3 }), clock);

        await limiter.WaitAsync(Ct);
        var second = limiter.WaitAsync(Ct);
        var third = limiter.WaitAsync(Ct);

        clock.Advance(TimeSpan.FromSeconds(2.9));
        second.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(0.1));
        await second;
        third.IsCompleted.ShouldBeFalse();
        clock.Advance(TimeSpan.FromSeconds(3));
        await third;
    }

    public void Dispose()
    {
        _hosts.ForEach(h => h.Dispose());
        _arxiv.Dispose();
        Directory.Delete(_cacheDir, recursive: true);
    }

    private ArxivPaper Paper(string arxivId) => new(
        arxivId, "Title", ["Ada Lovelace"], "Abstract.", ["cs.AI"], DateTimeOffset.UnixEpoch, $"{_arxiv.Url}/pdf/{arxivId}");

    private ArxivClient Client()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Arxiv:BaseUrl"] = $"{_arxiv.Url}/api/query",
            ["Arxiv:PdfCacheDir"] = _cacheDir,
            ["Arxiv:RateLimitDelaySeconds"] = "0",
            ["Arxiv:DownloadRetryDelayBaseSeconds"] = "0",
        });
        builder.Services.AddLogging();
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotArxiv();
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<ArxivClient>();
    }
}

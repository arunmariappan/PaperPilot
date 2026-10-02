using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Persistence;
using PaperPilot.Infrastructure.Search;
using PaperPilot.Ingestion;
using PaperPilot.IntegrationTests.Persistence;
using PaperPilot.IntegrationTests.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace PaperPilot.IntegrationTests.Ingestion;

/// <summary>
/// The whole ingestion job against the Postgres and OpenSearch containers, with WireMock standing in for the arXiv API
/// and PDFs, docling-serve and Jina. The recorded feed has three papers; two get the sample paper as their PDF and
/// one gets a 31-page PDF, which is skipped.
/// </summary>
[Collection(ContainersCollectionDefinition.Name)]
public sealed class IngestionJobTests(PostgresFixture postgres, OpenSearchFixture search) : IAsyncLifetime
{
    private const string Date = "20260930";
    private const string LongPaper = "2610.00834v1";

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private readonly WireMockServer _fakes = WireMockServer.Start();
    private readonly string _cache = Directory.CreateTempSubdirectory("paperpilot-ingestion-").FullName;
    private IHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await search.Client.EnsureIndexAsync(force: true, Ct);
        await search.Client.EnsureRrfPipelineAsync(cancellationToken: Ct);
        await using (var scope = postgres.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>();
            await db.Papers.ExecuteDeleteAsync(Ct);
            await db.IngestionRuns.ExecuteDeleteAsync(Ct);
        }

        var feed = (await File.ReadAllTextAsync(Path.Combine(Fixtures, "arxiv", "feed-cs-ai-20260930.xml"), Ct))
            .Replace("https://arxiv.org/pdf/", $"{_fakes.Url}/pdf/", StringComparison.Ordinal);
        _fakes.Given(Request.Create().WithPath("/api/query").UsingGet()).AtPriority(10).RespondWith(Response.Create().WithBody(feed));
        var samplePdf = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "docling", "sample-paper.pdf"), Ct);
        _fakes.Given(Request.Create().WithPath("/pdf/2610.00840v1")).RespondWith(Response.Create().WithBody(samplePdf));
        _fakes.Given(Request.Create().WithPath("/pdf/2610.00838v1")).RespondWith(Response.Create().WithBody(samplePdf));
        _fakes.Given(Request.Create().WithPath($"/pdf/{LongPaper}")).RespondWith(Response.Create().WithBody(Pdf(pages: 31)));
        _fakes.Given(Request.Create().WithPath("/health")).AtPriority(10).RespondWith(Response.Create().WithBody("""{"status":"ok"}"""));
        _fakes.Given(Request.Create().WithPath("/v1/convert/file").UsingPost()).AtPriority(10).RespondWith(Response.Create()
            .WithHeader("Content-Type", "application/json")
            .WithBody(await File.ReadAllTextAsync(Path.Combine(Fixtures, "docling", "sample-paper.json"), Ct)));
        _fakes.Given(Request.Create().WithPath("/v1/embeddings").UsingPost()).RespondWith(Response.Create().WithCallback(Embeddings));

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:papers"] = postgres.ConnectionString,
            ["OpenSearch:Host"] = search.Url,
            ["Arxiv:BaseUrl"] = $"{_fakes.Url}/api/query",
            ["Arxiv:PdfCacheDir"] = _cache,
            ["Arxiv:RateLimitDelaySeconds"] = "0",
            ["Arxiv:DownloadRetryDelayBaseSeconds"] = "0",
            ["Docling:BaseUrl"] = _fakes.Url,
            ["Jina:BaseUrl"] = $"{_fakes.Url}/v1/",
            ["Jina:ApiKey"] = "test-key",
        });
        builder.Services.AddLogging();
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotDatabase();
        builder.AddPaperPilotSearch();
        builder.AddPaperPilotIngestion();
        _host = builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        _fakes.Dispose();
        await Task.Run(() => Directory.Delete(_cache, recursive: true));
    }

    [Fact]
    public async Task A_run_stores_parses_and_indexes_the_days_papers()
    {
        await RunAsync();

        var papers = await QueryAsync(db => db.Papers.AsNoTracking().OrderBy(p => p.ArxivId).ToListAsync(Ct));
        papers.Select(p => (p.ArxivId, p.PdfProcessed)).ShouldBe(
            [(LongPaper, false), ("2610.00838v1", true), ("2610.00840v1", true)]);
        papers[0].ParserMetadata!.RootElement.GetProperty("note").GetString().ShouldBe(PaperRepository.ParseFailedNote);
        papers[2].Sections!.Select(s => s.Title).ShouldContain("1 Introduction");

        var run = (await RunsAsync()).ShouldHaveSingleItem();
        (run.Status, run.Trigger, run.TargetFrom, run.TargetTo).ShouldBe(
            (IngestionRunStatus.Succeeded, IngestionTriggers.Manual, new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 30)));
        (run.PapersFetched, run.PdfsDownloaded, run.PdfsParsed, run.PdfsSkipped, run.PapersStored).ShouldBe((3, 3, 2, 1, 3));
        run.ChunksIndexed.ShouldBeGreaterThan(0);
        (run.ChunksCreated, run.EmbeddingsGenerated).ShouldBe((run.ChunksIndexed, run.ChunksIndexed));
        run.Errors.ShouldBeEmpty();
        run.FinishedAt.ShouldNotBeNull();

        var indexed = await search.Client.CountAsync(Ct);
        (indexed, run.IndexDocCountAfter).ShouldBe((run.ChunksIndexed, run.ChunksIndexed));
        (await search.Client.CountUniquePapersAsync(Ct)).ShouldBe(2);
        (await search.Client.GetChunksByPaperAsync(LongPaper, Ct)).ShouldBeEmpty();
        _fakes.LogEntries.Count(e => e.RequestMessage!.Path == "/v1/convert/file").ShouldBe(2);
    }

    [Fact]
    public async Task Running_the_same_date_again_changes_nothing() // C5: idempotent re-indexing
    {
        await RunAsync();
        var first = await search.Client.CountAsync(Ct);

        await RunAsync();

        (await search.Client.CountAsync(Ct)).ShouldBe(first);
        (await QueryAsync(db => db.Papers.CountAsync(Ct))).ShouldBe(3);
        (await RunsAsync()).Select(r => r.Status).ShouldBe([IngestionRunStatus.Succeeded, IngestionRunStatus.Succeeded]);
        _fakes.LogEntries.Count(e => e.RequestMessage!.Path!.StartsWith("/pdf/", StringComparison.Ordinal)).ShouldBe(3); // cached
    }

    [Fact]
    public async Task A_run_fails_fast_when_docling_is_down()
    {
        _fakes.Given(Request.Create().WithPath("/health")).AtPriority(1).RespondWith(Response.Create().WithStatusCode(503));

        var error = await Should.ThrowAsync<IngestionFailedException>(RunAsync);

        error.Message.ShouldBe("Setup failed: docling-serve is not reachable");
        var run = (await RunsAsync()).ShouldHaveSingleItem();
        run.Status.ShouldBe(IngestionRunStatus.Failed);
        run.Errors.ShouldBe(["Setup failed: docling-serve is not reachable"]);
        (await QueryAsync(db => db.Papers.CountAsync(Ct))).ShouldBe(0);
    }

    [Fact]
    public async Task A_run_that_parses_no_pdfs_fails_but_keeps_the_metadata() // B15
    {
        _fakes.Given(Request.Create().WithPath("/v1/convert/file")).AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("conversion crashed"));

        var error = await Should.ThrowAsync<IngestionFailedException>(RunAsync);

        error.Message.ShouldBe("Fetched 3 papers but parsed none of their PDFs");
        var run = (await RunsAsync()).ShouldHaveSingleItem();
        run.Status.ShouldBe(IngestionRunStatus.Failed);
        run.Errors.Count(e => e.StartsWith("PDF parse failed:", StringComparison.Ordinal)).ShouldBe(2);
        (await QueryAsync(db => db.Papers.CountAsync(p => !p.PdfProcessed, Ct))).ShouldBe(3);
        (await search.Client.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_run_fails_when_arxiv_is_down()
    {
        _fakes.Given(Request.Create().WithPath("/api/query")).AtPriority(1).RespondWith(Response.Create().WithStatusCode(503));

        await Should.ThrowAsync<ArxivApiException>(RunAsync);

        var run = (await RunsAsync()).ShouldHaveSingleItem();
        run.Status.ShouldBe(IngestionRunStatus.Failed);
        run.Errors.ShouldBe(["arXiv API returned error 503"]);
    }

    private async Task RunAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DailyIngestionJob>()
            .RunAsync(Date, Date, IngestionTriggers.Manual, null, Ct);
    }

    private async Task<List<IngestionRun>> RunsAsync() =>
        await QueryAsync(db => db.IngestionRuns.AsNoTracking().OrderBy(r => r.StartedAt).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<PaperPilotDbContext, Task<T>> query)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>());
    }

    /// <summary>One deterministic unit vector per input text.</summary>
    private static ResponseMessage Embeddings(IRequestMessage request)
    {
        var inputs = JsonNode.Parse(request.Body!)!["input"]!.AsArray();
        var data = new JsonArray();
        for (var i = 0; i < inputs.Count; i++)
        {
            var vector = SampleChunks.Vector(StringComparer.Ordinal.GetHashCode(inputs[i]!.GetValue<string>()));
            data.Add(new JsonObject { ["index"] = i, ["embedding"] = new JsonArray([.. vector.Select(v => (JsonNode)v)]) });
        }

        var response = new ResponseMessage
        {
            StatusCode = 200,
            BodyData = new BodyData
            {
                BodyAsString = new JsonObject { ["model"] = "jina-embeddings-v3", ["data"] = data }.ToJsonString(),
                DetectedBodyType = BodyType.String,
                Encoding = Encoding.UTF8,
            },
        };
        response.AddHeader("Content-Type", "application/json");
        return response;
    }

    private static byte[] Pdf(int pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= pages; i++)
        {
            builder.AddPage(PageSize.A4).AddText($"Page {i}", 12, new PdfPoint(72, 720), font);
        }

        return builder.Build();
    }
}

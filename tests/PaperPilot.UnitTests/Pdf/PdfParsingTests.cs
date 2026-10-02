using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Pdf;
using PaperPilot.UnitTests.TestSupport;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace PaperPilot.UnitTests.Pdf;

public sealed class PdfParsingTests : IDisposable
{
    private static readonly string SamplePdf = Path.Combine(AppContext.BaseDirectory, "fixtures", "docling", "sample-paper.pdf");
    private static readonly string SampleConversion = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "docling", "sample-paper.json"));

    private readonly WireMockServer _docling = WireMockServer.Start();
    private readonly string _dir = Directory.CreateTempSubdirectory("paperpilot-pdf-").FullName;
    private readonly List<IHost> _hosts = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Docling_sections_match_python_docling() // same sample PDF, docling-serve 1.35 vs Python Docling 2.52
    {
        var conversion = DoclingServeClient.Read(JsonDocument.Parse(SampleConversion).RootElement);
        var expected = ParityFixtures.Load("docling/sample-paper-sections.json")["sections"]!.AsArray();

        var sections = DoclingSectionExtractor.Extract(conversion.Texts);

        sections.ShouldBe([.. expected.Select(s => new PaperSection(s!["title"]!.GetValue<string>(), s["content"]!.GetValue<string>()))]);
    }

    [Fact]
    public void Text_before_the_first_heading_is_a_content_section_and_empty_sections_are_dropped() =>
        DoclingSectionExtractor.Extract(
        [
            new("page_header", "Preprint. Under review."),
            new("section_header", "  1 Introduction "),
            new("section_header", "2 Empty"),
            new("text", "   "),
            new("section_header", "3 Method"),
            new("text", "First."),
            new("list_item", "Second."),
            new("caption", null),
        ]).ShouldBe(
        [
            new PaperSection("Content", "Preprint. Under review."),
            new PaperSection("3 Method", "First.\nSecond."),
        ]);

    [Fact]
    public void A_small_pdf_is_valid() =>
        Validator().Validate(WritePdf("one-page.pdf", pages: 1)).ShouldBe(new PdfValidation(PdfValidationOutcome.Valid, Pages: 1));

    [Fact]
    public void A_pdf_over_the_page_limit_is_skipped() =>
        Validator().Validate(WritePdf("long.pdf", pages: 31)).ShouldBe(
            new PdfValidation(PdfValidationOutcome.Skip, "PDF has too many pages: 31 > 30", 31));

    [Fact]
    public void A_file_over_the_size_limit_is_skipped()
    {
        var path = Path.Combine(_dir, "big.pdf");
        File.WriteAllBytes(path, [.. "%PDF-"u8, .. new byte[(1024 * 1024) + 1]]);

        Validator(maxFileSizeMb: 1).Validate(path).ShouldBe(new PdfValidation(PdfValidationOutcome.Skip, "PDF file too large: 1.0MB > 1.0MB"));
    }

    [Theory]
    [InlineData("", "PDF file is empty")]
    [InlineData("<html>not a pdf</html>", "File does not have PDF header")]
    [InlineData("%PDF-1.7 truncated garbage", "Error validating PDF")]
    public void A_file_that_is_not_a_usable_pdf_is_invalid(string content, string reason)
    {
        var path = Path.Combine(_dir, "bad.pdf");
        File.WriteAllText(path, content);

        var result = Validator().Validate(path);

        result.Outcome.ShouldBe(PdfValidationOutcome.Invalid);
        result.Reason.ShouldStartWith(reason);
    }

    [Fact]
    public async Task Docling_serve_gets_the_planned_options_and_its_sections_become_the_paper_content()
    {
        _docling.Given(Request.Create().WithPath("/v1/convert/file").UsingPost())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(SampleConversion));

        var result = await Parser().ParseAsync(SamplePdf, Ct);

        var content = result.Content.ShouldNotBeNull();
        (content.ParserUsed, content.Sections.Count, result.SkipReason).ShouldBe(("docling", 9, null));
        content.RawText.ShouldStartWith("Chunking Long Documents for Retrieval-Augmented Generation\n\nA. Example and B. Sample");
        content.Metadata["note"].ShouldBe("Content extracted from PDF, metadata comes from arXiv API");

        var request = _docling.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        var body = request.Body ?? System.Text.Encoding.Latin1.GetString(request.BodyAsBytes!);
        foreach (var field in new[]
        {
            ("to_formats", "json"), ("to_formats", "text"), ("do_ocr", "false"), ("do_table_structure", "true"),
            ("image_export_mode", "placeholder"), ("page_range", "1"), ("page_range", "30"), ("pdf_backend", "pypdfium2"),
        })
        {
            body.ShouldContain($"name={field.Item1}\r\n\r\n{field.Item2}\r\n");
        }

        body.ShouldContain("name=files; filename=sample-paper.pdf");
    }

    [Fact]
    public async Task A_partial_success_still_counts()
    {
        var partial = JsonNode.Parse(SampleConversion)!;
        partial["status"] = "partial_success";
        _docling.Given(Request.Create().WithPath("/v1/convert/file")).RespondWith(Response.Create().WithBody(partial.ToJsonString()));

        (await Parser().ParseAsync(SamplePdf, Ct)).Content.ShouldNotBeNull();
    }

    [Theory]
    [InlineData(200, """{"status":"failure","errors":[{"error_message":"Page 3 could not be read"}],"document":{}}""", "Docling conversion failure: Page 3 could not be read")]
    [InlineData(500, "Internal Server Error", "docling-serve returned 500: Internal Server Error")]
    public async Task A_failed_conversion_is_a_parsing_error(int status, string body, string message)
    {
        _docling.Given(Request.Create().WithPath("/v1/convert/file")).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        var error = await Should.ThrowAsync<PdfParsingException>(() => Parser().ParseAsync(SamplePdf, Ct));

        error.Message.ShouldBe(message);
    }

    [Fact]
    public async Task A_skipped_pdf_is_not_sent_to_docling()
    {
        var result = await Parser().ParseAsync(WritePdf("long.pdf", pages: 31), Ct);

        (result.Content, result.SkipReason).ShouldBe((null, "PDF has too many pages: 31 > 30"));
        _docling.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Docling_health_is_false_when_it_is_unreachable()
    {
        _docling.Given(Request.Create().WithPath("/health")).RespondWith(Response.Create().WithBody("""{"status":"ok"}"""));

        (await Client<DoclingServeClient>().HealthAsync(Ct)).ShouldBeTrue();
        (await Client<DoclingServeClient>(baseUrl: "http://127.0.0.1:1").HealthAsync(Ct)).ShouldBeFalse();
    }

    public void Dispose()
    {
        _hosts.ForEach(h => h.Dispose());
        _docling.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string WritePdf(string name, int pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        for (var i = 1; i <= pages; i++)
        {
            builder.AddPage(PageSize.A4).AddText($"Page {i}", 12, new PdfPoint(72, 720), font);
        }

        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private PdfValidator Validator(int? maxFileSizeMb = null) =>
        Client<PdfValidator>(maxFileSizeMb is { } mb ? new() { ["PdfParser:MaxFileSizeMb"] = mb.ToString(System.Globalization.CultureInfo.InvariantCulture) } : null);

    private PdfParser Parser() => Client<PdfParser>();

    private T Client<T>(Dictionary<string, string?>? settings = null, string? baseUrl = null)
        where T : notnull
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Docling:BaseUrl"] = baseUrl ?? _docling.Url });
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services.AddLogging();
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotPdfParsing();
        var host = builder.Build();
        _hosts.Add(host);
        return host.Services.GetRequiredService<T>();
    }
}

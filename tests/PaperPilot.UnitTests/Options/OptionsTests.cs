using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;

namespace PaperPilot.UnitTests.Options;

public sealed class OptionsTests
{
    [Fact]
    public void Defaults_match_the_Python_settings()
    {
        using var host = BuildHost();
        T Get<T>() where T : class => host.Services.GetRequiredService<IOptions<T>>().Value;

        var arxiv = Get<ArxivOptions>();
        arxiv.BaseUrl.ShouldBe("https://export.arxiv.org/api/query");
        arxiv.RateLimitDelaySeconds.ShouldBe(3.0);
        arxiv.TimeoutSeconds.ShouldBe(30);
        arxiv.MaxResults.ShouldBe(15);
        arxiv.SearchCategory.ShouldBe("cs.AI");
        arxiv.DownloadMaxRetries.ShouldBe(3);
        arxiv.DownloadRetryDelayBaseSeconds.ShouldBe(5.0);
        arxiv.MaxConcurrentDownloads.ShouldBe(5);
        arxiv.MaxConcurrentParsing.ShouldBe(1);

        var pdf = Get<PdfParserOptions>();
        (pdf.MaxPages, pdf.MaxFileSizeMb, pdf.DoOcr, pdf.DoTableStructure).ShouldBe((30, 20, false, true));

        var chunking = Get<ChunkingOptions>();
        (chunking.ChunkSize, chunking.OverlapSize, chunking.MinChunkSize, chunking.SectionBased)
            .ShouldBe((600, 100, 100, true));
        (chunking.SectionMinWords, chunking.SectionMaxWords).ShouldBe((100, 800));

        var openSearch = Get<OpenSearchOptions>();
        openSearch.ChunkIndexName.ShouldBe("arxiv-papers-chunks");
        (openSearch.VectorDimension, openSearch.VectorSpaceType).ShouldBe((1024, "cosinesimil"));
        (openSearch.RrfPipelineName, openSearch.HybridSearchSizeMultiplier).ShouldBe(("hybrid-rrf-pipeline", 2));

        var ollama = Get<OllamaOptions>();
        (ollama.TimeoutSeconds, ollama.Think).ShouldBe((300, false));
        Get<CacheOptions>().TtlHours.ShouldBe(6);
        Get<TelegramOptions>().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Binds_dotnet_style_keys()
    {
        using var host = BuildHost(new()
        {
            ["OpenSearch:IndexName"] = "papers-test",
            ["Chunking:ChunkSize"] = "300",
            ["Ollama:Think"] = "true",
        });

        host.Services.GetRequiredService<IOptions<OpenSearchOptions>>().Value.ChunkIndexName.ShouldBe("papers-test-chunks");
        host.Services.GetRequiredService<IOptions<ChunkingOptions>>().Value.ChunkSize.ShouldBe(300);
        host.Services.GetRequiredService<IOptions<OllamaOptions>>().Value.Think.ShouldBeTrue();
    }

    [Theory]
    [InlineData("100")]
    [InlineData("700")]
    public void Overlap_at_or_above_chunk_size_is_rejected(string overlap)
    {
        using var host = BuildHost(new() { ["Chunking:ChunkSize"] = "100", ["Chunking:OverlapSize"] = overlap });

        var error = Should.Throw<OptionsValidationException>(
            () => host.Services.GetRequiredService<IOptions<ChunkingOptions>>().Value);

        error.Message.ShouldContain("Overlap size must be less than chunk size");
    }

    [Fact]
    public async Task Invalid_configuration_stops_the_host_from_starting()
    {
        using var host = BuildHost(new() { ["OpenSearch:VectorSpaceType"] = "euclidean" });

        await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_Jina_key_is_a_warning_not_an_error()
    {
        using var host = BuildHost();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var warnings = Warnings(host);
        warnings.ShouldHaveSingleItem().ShouldStartWith("Jina:ApiKey is not set");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Telegram_and_Langfuse_keys_are_only_required_when_enabled()
    {
        using var host = BuildHost(new()
        {
            ["Jina:ApiKey"] = "key",
            ["Telegram:Enabled"] = "true",
            ["Langfuse:Enabled"] = "true",
        });

        await host.StartAsync(TestContext.Current.CancellationToken);

        Warnings(host).Count.ShouldBe(2);
        Warnings(host).ShouldContain(w => w.StartsWith("Telegram:BotToken is not set", StringComparison.Ordinal));
        Warnings(host).ShouldContain(w => w.StartsWith("Langfuse:PublicKey/SecretKey is not set", StringComparison.Ordinal));
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    private static IHost BuildHost(Dictionary<string, string?>? settings = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(settings ?? []);
        builder.Services.AddFakeLogging();
        builder.AddPaperPilotOptions();
        return builder.Build();
    }

    private static List<string> Warnings(IHost host) =>
        [.. host.Services.GetRequiredService<FakeLogCollector>().GetSnapshot()
            .Where(r => r.Level == LogLevel.Warning)
            .Select(r => r.Message)];
}

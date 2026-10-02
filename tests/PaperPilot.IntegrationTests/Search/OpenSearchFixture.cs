using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Search;

namespace PaperPilot.IntegrationTests.Search;

/// <summary>OpenSearch 2.19 (the image Aspire runs) with security off, plus a client built through the real registration.</summary>
public sealed class OpenSearchFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("opensearchproject/opensearch:2.19.0")
        .WithEnvironment("discovery.type", "single-node")
        .WithEnvironment("DISABLE_SECURITY_PLUGIN", "true")
        .WithEnvironment("DISABLE_INSTALL_DEMO_CONFIG", "true")
        .WithEnvironment("OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m")
        .WithPortBinding(9200, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
            .ForPort(9200)
            .ForPath("/_cluster/health")))
        .Build();

    private readonly List<IHost> _hosts = [];

    public string Url => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(9200)}";

    public OpenSearchClient Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Client = CreateClient();

        // The HTTP check passes as soon as the node answers; wait until the cluster is usable (green or yellow).
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        while (!await Client.HealthAsync(timeout.Token))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);
        }
    }

    /// <summary>A client for this cluster, or for another host or index when overridden.</summary>
    public OpenSearchClient CreateClient(string? host = null, string? indexName = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenSearch:Host"] = host ?? Url,
            ["OpenSearch:IndexName"] = indexName ?? "arxiv-papers",
        });
        builder.AddPaperPilotOptions();
        builder.AddPaperPilotSearch();
        var app = builder.Build();
        _hosts.Add(app);
        return app.Services.GetRequiredService<OpenSearchClient>();
    }

    public async ValueTask DisposeAsync()
    {
        _hosts.ForEach(h => h.Dispose());
        await _container.DisposeAsync();
    }
}

/// <summary>Three chunks from two papers, with deterministic random 1024-dimension unit vectors.</summary>
public static class SampleChunks
{
    public static readonly ChunkDocument Attention = Chunk(
        "2610.00001v1", 0, "Transformers use self-attention to model interactions between all tokens.", "Introduction",
        ["cs.CL"], new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), seed: 1);

    public static readonly ChunkDocument Positions = Chunk(
        "2610.00001v1", 1, "Positional encodings tell the transformer about token order.", "Method",
        ["cs.CL"], new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero), seed: 2);

    public static readonly ChunkDocument Reward = Chunk(
        "2610.00002v1", 0, "Reinforcement learning agents learn policies that maximise long-term reward.", "Background",
        ["cs.LG"], new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), seed: 3);

    public static IReadOnlyList<ChunkDocument> All { get; } = [Attention, Positions, Reward];

    private static ChunkDocument Chunk(
        string arxivId, int index, string text, string section, string[] categories, DateTimeOffset published, int seed) => new()
        {
            ArxivId = arxivId,
            PaperId = Guid.NewGuid().ToString(),
            ChunkIndex = index,
            ChunkText = text,
            ChunkWordCount = text.Split(' ').Length,
            StartChar = 0,
            EndChar = text.Length,
            SectionTitle = section,
            EmbeddingModel = "jina-embeddings-v3",
            Title = arxivId.StartsWith("2610.00001", StringComparison.Ordinal) ? "Attention Is All You Need" : "Learning to Act",
            Authors = "Ada Lovelace, Alan Turing",
            Abstract = "An abstract.",
            Categories = categories,
            PublishedDate = published,
            Embedding = Vector(seed),
        };

    public static float[] Vector(int seed)
    {
#pragma warning disable CA5394 // Test data, not security.
        var random = new Random(seed);
        var vector = Enumerable.Range(0, 1024).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
#pragma warning restore CA5394
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return [.. vector.Select(v => v / norm)];
    }
}

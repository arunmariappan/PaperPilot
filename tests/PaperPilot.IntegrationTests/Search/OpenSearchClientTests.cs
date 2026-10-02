using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Search;
using PaperPilot.IntegrationTests.Persistence;

namespace PaperPilot.IntegrationTests.Search;

[Collection(ContainersCollectionDefinition.Name)]
public sealed class OpenSearchClientTests(OpenSearchFixture search) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await search.Client.EnsureIndexAsync(force: true, Ct);
        await search.Client.EnsureRrfPipelineAsync(force: false, Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Index_and_pipeline_setup_is_idempotent()
    {
        (await search.Client.EnsureIndexAsync(cancellationToken: Ct)).ShouldBeFalse();
        (await search.Client.EnsureRrfPipelineAsync(cancellationToken: Ct)).ShouldBeFalse(); // B12
        (await search.Client.EnsureRrfPipelineAsync(force: true, Ct)).ShouldBeTrue();

        var stats = await search.Client.GetIndexStatsAsync(Ct);
        (stats.IndexName, stats.Exists, stats.DocumentCount).ShouldBe(("arxiv-papers-chunks", true, 0L));

        using var http = new HttpClient();
        var mapping = (await http.GetFromJsonAsync<JsonNode>($"{search.Url}/arxiv-papers-chunks/_mapping", Ct))!;
        var properties = mapping["arxiv-papers-chunks"]!["mappings"]!;
        properties["dynamic"]!.GetValue<string>().ShouldBe("strict");
        properties["properties"]!["embedding"]!["dimension"]!.GetValue<int>().ShouldBe(1024);
    }

    [Fact]
    public async Task Bm25_finds_chunks_by_keyword_with_highlights_and_filters()
    {
        var bulk = await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);
        (bulk.Succeeded, bulk.Failed).ShouldBe((3, 0));

        var result = await search.Client.SearchAsync(new SearchQuery("self-attention transformers"), cancellationToken: Ct);

        result.Mode.ShouldBe(SearchModes.Bm25);
        var top = result.Hits[0];
        (top.ChunkId, top.ArxivId, top.ChunkIndex, top.SectionTitle).ShouldBe(("2610.00001v1:0", "2610.00001v1", (int?)0, "Introduction"));
        top.Authors.ShouldBe("Ada Lovelace, Alan Turing");
        top.Highlights!["chunk_text"][0].ShouldContain("<mark>");

        var filtered = await search.Client.SearchAsync(new SearchQuery("transformers reward", Categories: ["cs.LG"]), cancellationToken: Ct);
        filtered.Hits.ShouldAllBe(h => h.ArxivId == "2610.00002v1");

        var newest = await search.Client.SearchAsync(new SearchQuery("   "), cancellationToken: Ct);
        newest.Total.ShouldBe(3);
        newest.Hits[0].ArxivId.ShouldBe("2610.00002v1");
    }

    [Fact]
    public async Task Hybrid_search_runs_through_the_rrf_pipeline()
    {
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);

        var result = await search.Client.SearchAsync(
            new SearchQuery("reward", Size: 3), SampleChunks.Reward.Embedding, Ct);

        result.Mode.ShouldBe(SearchModes.Hybrid);
        result.Hits[0].ChunkId.ShouldBe("2610.00002v1:0");
        result.Total.ShouldBe(result.Hits.Count);

        var none = await search.Client.SearchAsync(
            new SearchQuery("reward", MinScore: 10), SampleChunks.Reward.Embedding, Ct);
        (none.Total, none.Hits.Count).ShouldBe((0L, 0));
    }

    [Fact]
    public async Task Reindexing_the_same_chunks_does_not_duplicate_them() // C5
    {
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);

        (await search.Client.CountAsync(Ct)).ShouldBe(3);
        (await search.Client.CountUniquePapersAsync(Ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_a_paper_removes_only_its_chunks()
    {
        await search.Client.BulkIndexChunksAsync(SampleChunks.All, Ct);
        (await search.Client.GetChunksByPaperAsync("2610.00001v1", Ct)).Select(c => c.ChunkIndex).ShouldBe([0, 1]);

        (await search.Client.DeletePaperChunksAsync("2610.00001v1", Ct)).ShouldBe(2);

        (await search.Client.CountAsync(Ct)).ShouldBe(1);
        (await search.Client.GetChunksByPaperAsync("2610.00001v1", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_cluster_is_reported_as_unavailable_not_as_no_results() // B6
    {
        var client = search.CreateClient(host: "http://127.0.0.1:1");

        (await client.HealthAsync(Ct)).ShouldBeFalse();
        await Should.ThrowAsync<SearchUnavailableException>(() => client.SearchAsync(new SearchQuery("q"), cancellationToken: Ct));
    }

    [Fact]
    public async Task A_rejected_query_carries_the_opensearch_reason()
    {
        var client = search.CreateClient(indexName: "missing");

        var error = await Should.ThrowAsync<SearchQueryException>(() => client.SearchAsync(new SearchQuery("q"), cancellationToken: Ct));

        error.StatusCode.ShouldBe(404);
        error.Reason!.ShouldContain("index_not_found_exception");
    }
}

using PaperPilot.Core.Options;
using PaperPilot.Core.Search;
using PaperPilot.Infrastructure.Search;
using PaperPilot.UnitTests.TestSupport;

namespace PaperPilot.UnitTests.Search;

public sealed class IndexDefinitionsTests
{
    [Fact]
    public void Chunk_index_with_default_options_matches_the_python_mapping() =>
        JsonAssert.Equivalent(ParityFixtures.Load("search/chunks-index-mapping.json"), IndexDefinitions.ChunksIndex(new OpenSearchOptions()));

    [Fact]
    public void Vector_dimension_and_space_type_come_from_options() // B11
    {
        var index = IndexDefinitions.ChunksIndex(new OpenSearchOptions { VectorDimension = 768, VectorSpaceType = "l2" });

        var embedding = index["mappings"]!["properties"]!["embedding"]!;
        embedding["dimension"]!.GetValue<int>().ShouldBe(768);
        embedding["method"]!["space_type"]!.GetValue<string>().ShouldBe("l2");
        index["settings"]!["index.knn.space_type"]!.GetValue<string>().ShouldBe("l2");
    }

    [Fact]
    public void Rrf_pipeline_matches_the_python_definition()
    {
        var python = ParityFixtures.Load("search/rrf-pipeline.json").AsObject();
        python.Remove("id");

        JsonAssert.Equivalent(python, IndexDefinitions.RrfPipeline());
    }
}

public sealed class SearchHitMapperTests
{
    [Fact]
    public void Section_name_and_pdf_url_are_filled_in() // B7: Python returned null for both.
    {
        var hit = new ChunkHit(
            ChunkId: "2610.00791v1:3",
            ArxivId: "2610.00791v1",
            Title: "Enterprise Representation Simplification",
            Authors: "Terry Dorsey, Kevin Huggins",
            Abstract: "Enterprise information is represented ...",
            PublishedDate: "2026-09-30T22:31:40",
            ChunkText: "chunk",
            ChunkIndex: 3,
            SectionTitle: "Introduction",
            Score: 0.031,
            Highlights: new Dictionary<string, IReadOnlyList<string>> { ["chunk_text"] = ["<mark>chunk</mark>"] });

        var result = SearchHitMapper.ToSearchHit(hit);

        result.SectionName.ShouldBe("Introduction");
        result.PdfUrl.ShouldBe("https://arxiv.org/pdf/2610.00791.pdf");
        result.ChunkId.ShouldBe("2610.00791v1:3");
        result.Authors.ShouldBe("Terry Dorsey, Kevin Huggins");
        result.PublishedDate.ShouldBe("2026-09-30T22:31:40");
        result.Highlights!["chunk_text"].ShouldBe(["<mark>chunk</mark>"]);
    }
}

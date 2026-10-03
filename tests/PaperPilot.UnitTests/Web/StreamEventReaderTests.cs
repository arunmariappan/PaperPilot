using System.Text;
using PaperPilot.Web.Api;

namespace PaperPilot.UnitTests.Web;

public sealed class StreamEventReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recorded_answer_reads_as_metadata_then_chunks_then_done()
    {
        var events = await ReadAsync(Fixture("stream-answer.sse"));

        var metadata = events[0].ShouldBeOfType<StreamMetadata>();
        metadata.Sources.ShouldBe(["https://arxiv.org/pdf/2610.00820.pdf"]);
        metadata.ChunksUsed.ShouldBe(1);
        metadata.SearchMode.ShouldBe("hybrid");

        var done = events[^1].ShouldBeOfType<StreamDone>();
        done.Sources.ShouldBeNull();
        var chunks = events[1..^1];
        chunks.Length.ShouldBeGreaterThan(10);
        chunks.ShouldAllBe(e => e is StreamChunk);
        string.Concat(chunks.Cast<StreamChunk>().Select(c => c.Text)).ShouldBe(done.Answer);
    }

    [Fact]
    public async Task A_recorded_failure_ends_with_the_error()
    {
        var events = await ReadAsync(Fixture("stream-error.sse"));

        events.Length.ShouldBe(2);
        events[0].ShouldBeOfType<StreamMetadata>();
        events[1].ShouldBe(new StreamError("Response status code does not indicate success: 404 (Not Found)."));
    }

    [Fact]
    public async Task No_matching_chunks_is_a_done_event_with_empty_sources_not_metadata()
    {
        var events = await ReadAsync("""
            data: {"answer":"No relevant information found.","sources":[],"done":true}


            """);

        var done = events.ShouldHaveSingleItem().ShouldBeOfType<StreamDone>();
        done.Answer.ShouldBe("No relevant information found.");
        done.Sources.ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_and_unknown_events_are_skipped()
    {
        var events = await ReadAsync("""
            data: {not json

            data: {}

            event: other
            data: {"chunk":"Hi"}

            data: {"chunk":"never dispatched: a stream that ends mid-event drops it"}
            """);

        events.ShouldBe([new StreamChunk("Hi")]);
    }

    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "web", name));

    private static async Task<StreamEvent[]> ReadAsync(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return await StreamEventReader.ReadAsync(stream, Ct).ToArrayAsync(Ct);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Indexing;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Arxiv;
using PaperPilot.Infrastructure.Pdf;

namespace PaperPilot.Ingestion;

public static class IngestionRegistration
{
    /// <summary>
    /// Registers the arXiv client, PDF parsing, the chunker, the fetch and index services and <see cref="DailyIngestionJob"/>.
    /// Needs <c>AddPaperPilotOptions</c>, <c>AddPaperPilotDatabase</c> and <c>AddPaperPilotSearch</c>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotIngestion(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddPaperPilotArxiv();
        builder.AddPaperPilotPdfParsing();

        builder.Services.AddSingleton(services => new TextChunker(services.GetRequiredService<IOptions<ChunkingOptions>>().Value));
        builder.Services.AddScoped<PaperFetchService>();
        builder.Services.AddScoped<HybridIndexer>();
        builder.Services.AddScoped<DailyIngestionJob>();

        return builder;
    }
}

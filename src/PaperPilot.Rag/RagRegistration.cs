using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaperPilot.Rag.Retrieval;

namespace PaperPilot.Rag;

public static class RagRegistration
{
    /// <summary>
    /// Registers <see cref="RagService"/> and <see cref="IPaperRetriever"/>. They need search, the LLM and the
    /// cache: <c>AddPaperPilotSearch</c>, <c>AddPaperPilotLlm</c> and <c>AddPaperPilotCache</c>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotRag(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPaperRetriever, PaperRetriever>();
        builder.Services.AddSingleton<RagService>();

        return builder;
    }
}

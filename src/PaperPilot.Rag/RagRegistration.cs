using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Agentic.Executors;
using PaperPilot.Rag.Retrieval;

namespace PaperPilot.Rag;

public static class RagRegistration
{
    /// <summary>
    /// Registers <see cref="RagService"/>, <see cref="IAgenticRagService"/> and <see cref="IPaperRetriever"/>. They
    /// need search, the LLM and the cache: <c>AddPaperPilotSearch</c>, <c>AddPaperPilotLlm</c> and
    /// <c>AddPaperPilotCache</c>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotRag(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPaperRetriever, PaperRetriever>();
        builder.Services.AddSingleton<RagService>();
        builder.Services.AddAgenticRag();

        return builder;
    }

    /// <summary>The agent workflow: its executors (stateless singletons), the built workflow, and the service.</summary>
    internal static IServiceCollection AddAgenticRag(this IServiceCollection services)
    {
        services.AddSingleton<GuardrailExecutor>();
        services.AddSingleton<RetrieveExecutor>();
        services.AddSingleton<GradeExecutor>();
        services.AddSingleton<RewriteExecutor>();
        services.AddSingleton<GenerateExecutor>();
        services.AddSingleton<OutOfScopeExecutor>();
        services.AddSingleton<MaxAttemptsExecutor>();
        services.AddSingleton<SearchUnavailableExecutor>();
        services.AddSingleton<AgenticRagWorkflow>();
        services.AddSingleton<IAgenticRagService, AgenticRagService>();
        return services;
    }
}

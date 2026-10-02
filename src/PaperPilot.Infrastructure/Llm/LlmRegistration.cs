using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OllamaSharp;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Http;

namespace PaperPilot.Infrastructure.Llm;

public static class LlmRegistration
{
    /// <summary>The <c>ActivitySource</c> of the chat client's GenAI spans (one per LLM call).</summary>
    public const string ActivitySourceName = "PaperPilot.Llm";

    internal const string HttpClientName = "ollama";

    /// <summary>
    /// Registers Ollama (<c>ConnectionStrings:ollama</c>) as <see cref="IChatClient"/>, with OpenTelemetry spans that
    /// include prompts and completions (Langfuse shows them; fine for a local app) and logging.
    /// The HTTP client has no retries and times out after <c>Ollama:TimeoutSeconds</c> (plan R1): a retried
    /// minutes-long generation is worse than a clean failure.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotLlm(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var endpoint = OllamaEndpoint.FromConfiguration(builder.Configuration);
        builder.Services.AddHttpClient(HttpClientName, http => http.BaseAddress = endpoint)
            .WithoutResilience(services =>
                TimeSpan.FromSeconds(services.GetRequiredService<IOptions<OllamaOptions>>().Value.TimeoutSeconds));

        builder.Services.AddSingleton<IOllamaApiClient>(services =>
        {
            var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            if (http.BaseAddress is null)
            {
                throw new InvalidOperationException($"ConnectionStrings:{OllamaEndpoint.ConnectionName} is not set.");
            }

            return new OllamaApiClient(http, services.GetRequiredService<IOptions<OllamaOptions>>().Value.Model);
        });

        builder.Services.AddChatClient(services => (IChatClient)services.GetRequiredService<IOllamaApiClient>())
            .UseOpenTelemetry(sourceName: ActivitySourceName, configure: telemetry => telemetry.EnableSensitiveData = true)
            .UseLogging();

        builder.Services.AddSingleton<ChatOptionsFactory>();
        builder.Services.AddSingleton<OllamaModelCatalog>();

        return builder;
    }
}

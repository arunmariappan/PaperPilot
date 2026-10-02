using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace PaperPilot.Infrastructure.Http;

/// <summary>
/// Replacements for the standard resilience handler that <c>AddServiceDefaults</c> puts on every client
/// (10 s per attempt, ~30 s in total), for clients that need different limits.
/// </summary>
public static class LongRunningHttpClientExtensions
{
    /// <summary>
    /// For calls that legitimately take minutes (Ollama generation, docling parsing, Jina 429 back-off): a pipeline whose
    /// only overall limit is <paramref name="totalTimeout"/>.
    /// </summary>
    /// <param name="builder">The named or typed client.</param>
    /// <param name="pipelineName">A name for the pipeline, unique per client.</param>
    /// <param name="totalTimeout">The limit for the whole call, retries and back-off included.</param>
    /// <param name="configure">
    /// Adds strategies inside the total timeout, such as a retry for 429 responses. They run in the order they're added.
    /// </param>
    public static IHttpClientBuilder AddLongRunningResilienceHandler(
        this IHttpClientBuilder builder,
        string pipelineName,
        TimeSpan totalTimeout,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>>? configure = null)
    {
        RemoveResilienceAndClientTimeout(builder);

        builder.AddResilienceHandler(pipelineName, pipeline =>
        {
            pipeline.AddTimeout(totalTimeout);
            configure?.Invoke(pipeline);
        });

        return builder;
    }

    /// <summary>The standard handler, with limits set by <paramref name="configure"/>.</summary>
    public static IHttpClientBuilder ReplaceStandardResilienceHandler(
        this IHttpClientBuilder builder, Action<HttpStandardResilienceOptions> configure)
    {
        RemoveResilienceAndClientTimeout(builder);
        builder.AddStandardResilienceHandler(configure);
        return builder;
    }

    /// <summary>For quick probes such as health checks: no retries, just <paramref name="timeout"/>.</summary>
    public static IHttpClientBuilder WithoutResilience(this IHttpClientBuilder builder, TimeSpan timeout) =>
        builder.WithoutResilience(_ => timeout);

    /// <summary>
    /// No retries, and <c>HttpClient.Timeout</c> from <paramref name="timeout"/>, e.g. a value from options. For calls
    /// where a retry costs more than a clean failure, like a minutes-long LLM generation.
    /// </summary>
    public static IHttpClientBuilder WithoutResilience(this IHttpClientBuilder builder, Func<IServiceProvider, TimeSpan> timeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(timeout);

        // Experimental in Microsoft.Extensions.Http.Resilience 10.x, but it is the supported way to drop the default handler.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        builder.ConfigureHttpClient((services, client) => client.Timeout = timeout(services));
        return builder;
    }

    private static void RemoveResilienceAndClientTimeout(IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // HttpClient.Timeout (100 s by default) applies on top of any pipeline, so the pipeline must own the limit.
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}

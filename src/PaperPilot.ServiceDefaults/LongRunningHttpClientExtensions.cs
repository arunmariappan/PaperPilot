using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// For HTTP clients whose calls legitimately take minutes (Ollama generation, docling parsing, Jina 429 back-off).
/// </summary>
public static class LongRunningHttpClientExtensions
{
    /// <summary>
    /// Replaces the standard resilience handler that <c>AddServiceDefaults</c> puts on every client
    /// (10 s per attempt, ~30 s in total) with a pipeline that has <paramref name="totalTimeout"/> as its only overall limit.
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
        ArgumentNullException.ThrowIfNull(builder);

        // Experimental in Microsoft.Extensions.Http.Resilience 10.x, but it is the supported way to drop the default handler.
#pragma warning disable EXTEXP0001
        builder.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        // HttpClient.Timeout (100 s by default) applies on top of any pipeline, so the pipeline must own the limit.
        builder.ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan);

        builder.AddResilienceHandler(pipelineName, pipeline =>
        {
            pipeline.AddTimeout(totalTimeout);
            configure?.Invoke(pipeline);
        });

        return builder;
    }
}

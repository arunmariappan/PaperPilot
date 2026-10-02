using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PaperPilot.Infrastructure.Http;

namespace PaperPilot.Infrastructure.Arxiv;

public static class ArxivRegistration
{
    /// <summary>
    /// Registers <see cref="ArxivClient"/>. Its HTTP client has no standard resilience handler (R1): the client sets
    /// its own timeouts and retries downloads itself, and every request waits for the shared <see cref="ArxivRateLimiter"/>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotArxiv(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ArxivRateLimiter>();
        builder.Services.AddTransient<ArxivRateLimitHandler>();
        builder.Services.AddHttpClient<ArxivClient>(http =>
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PaperPilot/0.1 (+https://github.com/arunmariappan/PaperPilot)"))
            .WithoutResilience(Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<ArxivRateLimitHandler>();

        return builder;
    }
}

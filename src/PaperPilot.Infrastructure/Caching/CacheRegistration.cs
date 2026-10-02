using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PaperPilot.Core.Caching;

namespace PaperPilot.Infrastructure.Caching;

public static class CacheRegistration
{
    public const string ConnectionName = "redis";

    /// <summary>
    /// Registers the answer cache on Redis (<c>ConnectionStrings:redis</c>) through Aspire's client, which adds tracing
    /// and a health check, and doesn't fail startup when Redis is down.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotCache(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddRedisClient(ConnectionName);
        builder.Services.AddSingleton<IAnswerCache, AnswerCache>();

        return builder;
    }
}

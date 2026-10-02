using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PaperPilot.Infrastructure.Persistence;

public static class PersistenceRegistration
{
    /// <summary>The Aspire connection name (<c>ConnectionStrings:papers</c>).</summary>
    public const string ConnectionName = "papers";

    /// <summary>
    /// Registers a pooled <see cref="PaperPilotDbContext"/> on the <c>papers</c> connection, enriched by Aspire
    /// (retries, health check, tracing), <see cref="PaperRepository"/> and <see cref="IngestionRunRepository"/>.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotDatabase(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<TimestampInterceptor>();

        builder.Services.AddDbContextPool<PaperPilotDbContext>((services, options) =>
        {
            var connectionString = builder.Configuration.GetConnectionString(ConnectionName)
                ?? throw new InvalidOperationException(
                    $"ConnectionStrings:{ConnectionName} is not set. Run the service through the AppHost.");
            PaperPilotDbContext.Configure(options, connectionString)
                .AddInterceptors(services.GetRequiredService<TimestampInterceptor>());
        });
        builder.EnrichNpgsqlDbContext<PaperPilotDbContext>();

        builder.Services.AddScoped<PaperRepository>();
        builder.Services.AddScoped<IngestionRunRepository>();

        return builder;
    }
}

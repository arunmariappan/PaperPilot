using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Sends PaperPilot's own spans to Langfuse over OTLP/HTTP when <c>Langfuse:Enabled</c> is on and the keys are set.
/// Langfuse gets a tracer provider of its own that listens only to <see cref="Sources"/>, so HTTP, EF Core and Redis
/// spans stay out of it, while the main pipeline (and the Aspire dashboard) still gets every span.
/// </summary>
public static class LangfuseExporter
{
    /// <summary>The activity sources whose spans go to Langfuse: RAG, LLM and agent spans.</summary>
    public static IReadOnlyList<string> Sources { get; } = ["PaperPilot.*", "Microsoft.Agents.AI*"];

    public static TBuilder AddLangfuseExporter<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var langfuse = builder.Configuration.GetSection("Langfuse");
        var publicKey = langfuse["PublicKey"];
        var secretKey = langfuse["SecretKey"];
        if (!langfuse.GetValue("Enabled", false) || string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            return builder;
        }

        var baseUrl = (langfuse["BaseUrl"] ?? "http://localhost:3010").TrimEnd('/');
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));

        builder.Services.AddSingleton(_ =>
        {
            // With an explicit endpoint the exporter doesn't append /v1/traces itself.
            var exporter = new OtlpTraceExporter(new OtlpExporterOptions
            {
                Endpoint = new Uri($"{baseUrl}/api/public/otel/v1/traces"),
                Protocol = OtlpExportProtocol.HttpProtobuf,
                Headers = $"Authorization=Basic {credentials}",
            });

            // The default resource reads OTEL_SERVICE_NAME, which Aspire sets, so Langfuse sees the same service name.
            return new LangfuseTracing(Sdk.CreateTracerProviderBuilder()
                .AddSource([.. Sources])
                .AddProcessor(new BatchActivityExportProcessor(exporter))
                .Build());
        });
        builder.Services.AddHostedService<LangfuseTracingStarter>();

        return builder;
    }
}

/// <summary>Owns the tracer provider that exports to Langfuse; disposing it flushes the last spans.</summary>
public sealed class LangfuseTracing(TracerProvider provider) : IDisposable
{
    public TracerProvider Provider => provider;

    public void Dispose() => provider.Dispose();
}

/// <summary>Creates <see cref="LangfuseTracing"/> when the host starts, so it listens from the first request.</summary>
internal sealed class LangfuseTracingStarter(LangfuseTracing tracing) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GC.KeepAlive(tracing);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

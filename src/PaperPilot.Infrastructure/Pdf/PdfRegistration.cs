using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using PaperPilot.Infrastructure.Http;
using Polly;
using Polly.Retry;

namespace PaperPilot.Infrastructure.Pdf;

public static class PdfRegistration
{
    /// <summary>
    /// Registers <see cref="PdfParser"/>, <see cref="PdfValidator"/> and <see cref="DoclingServeClient"/>. A conversion
    /// may take <c>Docling:TimeoutSeconds</c> (600) and is retried once, only when docling-serve can't be reached (R1, R3).
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotPdfParsing(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Docling:TimeoutSeconds", new DoclingOptions().TimeoutSeconds));
        builder.Services.AddHttpClient<DoclingServeClient>((services, http) =>
                http.BaseAddress = new Uri(services.GetRequiredService<IOptions<DoclingOptions>>().Value.BaseUrl.TrimEnd('/') + "/"))
            .AddLongRunningResilienceHandler("docling", timeout, pipeline => pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>(ex => ex.HttpRequestError == HttpRequestError.ConnectionError),
                MaxRetryAttempts = 1,
                Delay = TimeSpan.FromSeconds(2),
            }));

        builder.Services.AddSingleton<PdfValidator>();
        builder.Services.AddSingleton<PdfParser>();

        return builder;
    }
}

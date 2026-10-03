using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PaperPilot.Core.Options;

public static class OptionsRegistration
{
    /// <summary>
    /// Binds every PaperPilot options section and validates it when the host starts, so bad configuration fails fast.
    /// Missing API keys are not errors: <see cref="MissingSecretsReporter"/> logs a warning for each one instead.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotOptions(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddValidatedOptions<AppOptions>(AppOptions.SectionName);
        builder.AddValidatedOptions<ArxivOptions>(ArxivOptions.SectionName);
        builder.AddValidatedOptions<PdfParserOptions>(PdfParserOptions.SectionName);
        builder.AddValidatedOptions<DoclingOptions>(DoclingOptions.SectionName);
        builder.AddValidatedOptions<ChunkingOptions>(ChunkingOptions.SectionName);
        builder.AddValidatedOptions<OpenSearchOptions>(OpenSearchOptions.SectionName);
        builder.AddValidatedOptions<JinaOptions>(JinaOptions.SectionName);
        builder.AddValidatedOptions<OllamaOptions>(OllamaOptions.SectionName);
        builder.AddValidatedOptions<CacheOptions>(CacheOptions.SectionName);
        builder.AddValidatedOptions<AgenticRagOptions>(AgenticRagOptions.SectionName);
        builder.AddValidatedOptions<LangfuseOptions>(LangfuseOptions.SectionName);
        builder.AddValidatedOptions<TelegramOptions>(TelegramOptions.SectionName);

        builder.Services.AddHostedService<MissingSecretsReporter>();

        return builder;
    }

    private static void AddValidatedOptions<TOptions>(this IHostApplicationBuilder builder, string sectionName)
        where TOptions : class =>
        builder.Services.AddOptions<TOptions>()
            .Bind(builder.Configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
}

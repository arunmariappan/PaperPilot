using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PaperPilot.Core.Options;

/// <summary>
/// Logs one warning at startup for each API key a feature needs but doesn't have. The service still starts, and
/// only that feature degrades.
/// </summary>
internal sealed partial class MissingSecretsReporter(
    IOptions<JinaOptions> jina,
    IOptions<TelegramOptions> telegram,
    IOptions<LangfuseOptions> langfuse,
    ILogger<MissingSecretsReporter> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jina.Value.ApiKey))
        {
            LogMissing("Jina:ApiKey", "hybrid search falls back to BM25 and ingestion can't embed chunks");
        }

        if (telegram.Value.Enabled && string.IsNullOrWhiteSpace(telegram.Value.BotToken))
        {
            LogMissing("Telegram:BotToken", "the Telegram bot won't start");
        }

        if (langfuse.Value.Enabled
            && (string.IsNullOrWhiteSpace(langfuse.Value.PublicKey) || string.IsNullOrWhiteSpace(langfuse.Value.SecretKey)))
        {
            LogMissing("Langfuse:PublicKey/SecretKey", "traces aren't sent to Langfuse and /feedback is unavailable");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Setting} is not set: {Consequence}")]
    private partial void LogMissing(string setting, string consequence);
}

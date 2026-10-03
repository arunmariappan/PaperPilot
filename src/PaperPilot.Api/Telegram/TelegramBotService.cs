using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PaperPilot.Api.Telegram;

/// <summary>
/// Runs the Telegram bot inside the API, as Python ran it in the FastAPI lifespan: long polling (no webhook, so no
/// public URL), one update at a time. It runs only when <c>Telegram:Enabled</c> is true and a token is set; a failure
/// to start is logged and never stops the API.
/// </summary>
internal sealed partial class TelegramBotService(
    IOptions<TelegramOptions> options, IServiceProvider services, TimeProvider time, ILogger<TelegramBotService> logger)
    : BackgroundService
{
    /// <summary>Pause after a failed <c>getUpdates</c>, so an outage doesn't turn into a tight retry loop.</summary>
    internal static readonly TimeSpan PollingRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.BotToken))
        {
            LogNotConfigured(logger);
            return;
        }

        ITelegramBotClient client;
        PollingHandler handler;
        try
        {
            client = services.GetRequiredService<ITelegramBotClient>();
            var username = (await client.GetMe(stoppingToken)).Username;
            handler = new PollingHandler(services.GetRequiredService<TelegramUpdateHandler>(), username, time, logger);

            // Long polling fails while a webhook is set. Pending updates are kept, as Python's start_polling() did.
            await client.DeleteWebhook(dropPendingUpdates: false, cancellationToken: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            LogStartFailed(logger, ex.Message, ex);
            return;
        }

        LogStarted(logger, handler.Username);
        try
        {
            await client.ReceiveAsync(handler, new ReceiverOptions { AllowedUpdates = [UpdateType.Message] }, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            LogPollingStopped(logger, ex);
        }

        LogStopped(logger);
    }

    /// <summary>Hands each message to <see cref="TelegramUpdateHandler"/> and logs polling errors.</summary>
    private sealed class PollingHandler(TelegramUpdateHandler handler, string? username, TimeProvider time, ILogger logger)
        : IUpdateHandler
    {
        public string? Username => username;

        public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Message is { } message)
            {
                await handler.HandleAsync(message, username, cancellationToken);
            }
        }

        public async Task HandleErrorAsync(
            ITelegramBotClient botClient, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
        {
            if (exception is ApiRequestException { ErrorCode: 409 })
            {
                // Telegram allows one getUpdates caller per token, e.g. not the Python stack's bot at the same time.
                LogConflict(logger, exception.Message);
            }
            else
            {
                LogPollingError(logger, source, exception.Message, exception);
            }

            if (source == HandleErrorSource.PollingError)
            {
                await Task.Delay(PollingRetryDelay, time, cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram bot not configured - skipping initialization")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram bot started successfully (@{Username})")]
    private static partial void LogStarted(ILogger logger, string? username);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to start Telegram bot: {Reason}")]
    private static partial void LogStartFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram polling stopped unexpectedly")]
    private static partial void LogPollingStopped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telegram bot stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram polling error ({Source}): {Reason}")]
    private static partial void LogPollingError(ILogger logger, HandleErrorSource source, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Another process is polling this bot's updates: {Reason}")]
    private static partial void LogConflict(ILogger logger, string reason);
}

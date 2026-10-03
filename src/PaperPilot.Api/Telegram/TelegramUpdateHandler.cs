using PaperPilot.Core.Contracts;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Text;
using PaperPilot.Rag;
using PaperPilot.Rag.Agentic;
using PaperPilot.Rag.Retrieval;
using PaperPilot.Rag.Telemetry;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PaperPilot.Api.Telegram;

/// <summary>
/// Answers one Telegram message: <c>/start</c>, <c>/help</c>, <c>/search</c>, <c>/agent</c> (N5), or a question. Questions
/// go through the shared <see cref="RagService"/>, so the answer cache and tracing apply (C12). Other commands, and
/// commands addressed to another bot, are ignored, as python-telegram-bot's handlers did.
/// </summary>
internal sealed partial class TelegramUpdateHandler(
    ITelegramSender sender,
    RagService rag,
    IPaperRetriever retriever,
    IAgenticRagService agent,
    TimeProvider time,
    ILogger<TelegramUpdateHandler> logger)
{
    /// <summary>Telegram shows "typing…" for five seconds, so it's resent while an answer is being generated.</summary>
    internal static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);

    /// <summary>Chunks <c>/search</c> retrieves before keeping the first five distinct papers.</summary>
    internal const int SearchSize = 10;

    private const int QuestionTopK = 3;

    /// <summary>Handles a message. Failures are logged and answered; only cancellation escapes.</summary>
    /// <param name="message">The incoming message; only text messages are answered.</param>
    /// <param name="botUsername">The bot's username, for commands written as <c>/help@name</c>.</param>
    /// <param name="cancellationToken">Stops the handler when the bot shuts down.</param>
    public async Task HandleAsync(Message message, string? botUsername, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Text is not { } text)
        {
            return;
        }

        var chatId = message.Chat.Id;
        try
        {
            if (!IsCommand(message))
            {
                await AnswerQuestionAsync(chatId, text, cancellationToken);
                return;
            }

            var (command, arguments) = ParseCommand(message, text, botUsername);
            switch (command)
            {
                case "start":
                    await sender.SendTextAsync(chatId, TelegramMessages.Start, markdown: false, cancellationToken);
                    break;
                case "help":
                    await sender.SendTextAsync(chatId, TelegramMessages.Help, markdown: false, cancellationToken);
                    break;
                case "search":
                    await SearchAsync(chatId, arguments, cancellationToken);
                    break;
                case "agent":
                    await AskAgentAsync(chatId, arguments, cancellationToken);
                    break;
                default:
                    LogIgnoredCommand(logger, command);
                    break;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogHandlingFailed(logger, ex);
        }
    }

    private async Task AnswerQuestionAsync(long chatId, string question, CancellationToken cancellationToken)
    {
        var (reply, markdown) = await WhileTypingAsync(chatId, async () =>
        {
            try
            {
                var request = new AskRequest { Query = question, TopK = QuestionTopK, UseHybrid = true };
                var response = await rag.AskAsync(request, UserId(chatId), cancellationToken);
                return response.ChunksUsed == 0
                    ? (TelegramMessages.NoRelevantPapers, false)
                    : (TelegramMessageFormatter.Answer(response), true);
            }
            catch (SearchUnavailableException ex)
            {
                LogSearchUnavailable(logger, ex);
                return (TelegramMessages.SearchUnavailable, false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogQuestionFailed(logger, ex);
                return (TelegramMessages.Error(ex.Message), false);
            }
        }, cancellationToken);

        await ReplyAsync(chatId, reply, markdown, cancellationToken);
    }

    private async Task SearchAsync(long chatId, string keywords, CancellationToken cancellationToken)
    {
        if (keywords.Length == 0)
        {
            await sender.SendTextAsync(chatId, TelegramMessages.SearchUsage, markdown: false, cancellationToken);
            return;
        }

        var reply = await WhileTypingAsync(chatId, async () =>
        {
            using var activity = RagTelemetry.StartRequest("telegram_search", keywords, UserId(chatId));
            try
            {
                var retrieval = await retriever.RetrieveAsync(keywords, SearchSize, useHybrid: true, categories: null, cancellationToken);
                activity.SetTraceOutput(new { ChunksReturned = retrieval.Chunks.Count, retrieval.SearchMode });
                return TelegramMessageFormatter.SearchResults(retrieval.Chunks);
            }
            catch (SearchUnavailableException ex)
            {
                LogSearchUnavailable(logger, ex);
                activity.Fail(ex);
                return TelegramMessages.SearchUnavailable;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogSearchFailed(logger, ex);
                activity.Fail(ex);
                return TelegramMessages.SearchFailed(ex.Message);
            }
        }, cancellationToken);

        await ReplyAsync(chatId, reply, markdown: false, cancellationToken);
    }

    private async Task AskAgentAsync(long chatId, string question, CancellationToken cancellationToken)
    {
        if (question.Length == 0)
        {
            await sender.SendTextAsync(chatId, TelegramMessages.AgentUsage, markdown: false, cancellationToken);
            return;
        }

        var (reply, markdown) = await WhileTypingAsync(chatId, async () =>
        {
            try
            {
                var response = await agent.AskAsync(new AskRequest { Query = question }, UserId(chatId), cancellationToken);
                return (TelegramMessageFormatter.AgentAnswer(response), true);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogQuestionFailed(logger, ex);
                return (TelegramMessages.Error(ex.Message), false);
            }
        }, cancellationToken);

        await ReplyAsync(chatId, reply, markdown, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="text"/> in parts of at most 4,096 characters (B19). A part Telegram rejects as Markdown is
    /// sent again as plain text.
    /// </summary>
    private async Task ReplyAsync(long chatId, string text, bool markdown, CancellationToken cancellationToken)
    {
        foreach (var part in TelegramMessageFormatter.Split(text))
        {
            if (!markdown)
            {
                await sender.SendTextAsync(chatId, part, markdown: false, cancellationToken);
                continue;
            }

            try
            {
                await sender.SendTextAsync(chatId, part, markdown: true, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                LogMarkdownRejected(logger, ex.Message);
                await sender.SendTextAsync(chatId, part, markdown: false, cancellationToken);
            }
        }
    }

    /// <summary>Runs <paramref name="work"/> while the chat shows "typing…", which stops before the reply is sent.</summary>
    private async Task<T> WhileTypingAsync<T>(long chatId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var keepTyping = KeepTypingAsync(chatId, typing.Token);
        try
        {
            return await work();
        }
        finally
        {
            await typing.CancelAsync();
            await keepTyping;
        }
    }

    private async Task KeepTypingAsync(long chatId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await sender.SendTypingAsync(chatId, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogTypingFailed(logger, ex.Message);
                }

                await Task.Delay(TypingInterval, time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The work is done.
        }
    }

    /// <summary>A command is a message that starts with a bot-command entity, as python-telegram-bot decided.</summary>
    private static bool IsCommand(Message message) =>
        message.Entities is [{ Type: MessageEntityType.BotCommand, Offset: 0 }, ..];

    /// <summary>
    /// The command in lower case (null when it's addressed to another bot) and its arguments: the rest of the text
    /// split on whitespace and joined with single spaces, like python-telegram-bot's <c>context.args</c>.
    /// </summary>
    private static (string? Command, string Arguments) ParseCommand(Message message, string text, string? botUsername)
    {
        var entity = message.Entities![0];
        var parts = text[1..Math.Min(entity.Length, text.Length)].Split('@', 2);
        var forUs = parts.Length == 1 || string.Equals(parts[1], botUsername, StringComparison.OrdinalIgnoreCase);
        var arguments = string.Join(' ', PythonText.Split(text).Skip(1));
        return (forUs ? PythonText.Lower(parts[0]) : null, arguments);
    }

    private static string UserId(long chatId) => $"telegram:{chatId}";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignoring Telegram command '{Command}'")]
    private static partial void LogIgnoredCommand(ILogger logger, string? command);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telegram message handling failed")]
    private static partial void LogHandlingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Question handling failed")]
    private static partial void LogQuestionFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search failed")]
    private static partial void LogSearchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Search is unavailable")]
    private static partial void LogSearchUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telegram rejected the Markdown, resending as plain text: {Reason}")]
    private static partial void LogMarkdownRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't send the typing action: {Reason}")]
    private static partial void LogTypingFailed(ILogger logger, string reason);
}

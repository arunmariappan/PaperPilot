using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PaperPilot.Api.Telegram;

/// <summary>
/// What the bot sends. A thin wrapper over the Telegram client, whose send methods are extension methods that tests
/// can't fake.
/// </summary>
internal interface ITelegramSender
{
    /// <summary>Sends a message with link previews off; <paramref name="markdown"/> uses legacy Markdown.</summary>
    Task SendTextAsync(long chatId, string text, bool markdown, CancellationToken cancellationToken);

    /// <summary>Shows "typing…" in the chat for about five seconds, or until the next message.</summary>
    Task SendTypingAsync(long chatId, CancellationToken cancellationToken);
}

internal sealed class TelegramSender(ITelegramBotClient client) : ITelegramSender
{
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    public async Task SendTextAsync(long chatId, string text, bool markdown, CancellationToken cancellationToken) =>
        await client.SendMessage(
            chatId,
            text,
            markdown ? ParseMode.Markdown : ParseMode.None,
            linkPreviewOptions: NoPreview,
            cancellationToken: cancellationToken);

    public Task SendTypingAsync(long chatId, CancellationToken cancellationToken) =>
        client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: cancellationToken);
}

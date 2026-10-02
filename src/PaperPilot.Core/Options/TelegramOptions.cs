namespace PaperPilot.Core.Options;

/// <summary>The Telegram bot. It runs only when enabled and given a token.</summary>
public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;
}

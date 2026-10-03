using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.Http;
using PaperPilot.Core.Options;
using Telegram.Bot;

namespace PaperPilot.Api.Telegram;

internal static partial class TelegramRegistration
{
    /// <summary>
    /// Registers the bot. <see cref="TelegramBotService"/> decides at startup whether it runs (<c>Telegram:Enabled</c> and
    /// a token), so nothing touches Telegram otherwise.
    /// </summary>
    public static IHostApplicationBuilder AddPaperPilotTelegram(this IHostApplicationBuilder builder)
    {
        // The client gets its own HttpClient: a factory client would log every request URL, and its standard
        // resilience handler would cut long polls off after 10 seconds.
        builder.Services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TelegramOptions>>().Value;
            return new TelegramBotClient(new TelegramBotClientOptions(options.BotToken, options.BaseUrl));
        });
        builder.Services.AddSingleton<ITelegramSender, TelegramSender>();
        builder.Services.AddSingleton<TelegramUpdateHandler>();
        builder.Services.AddHostedService<TelegramBotService>();
        builder.Services.Configure<HttpClientTraceInstrumentationOptions>(HideBotToken);
        return builder;
    }

    /// <summary>
    /// Telegram's Bot API puts the token in the URL path (<c>/bot{token}/{method}</c>). Long polls aren't traced at
    /// all (one every few seconds, all empty), and other calls are recorded with the token replaced.
    /// </summary>
    internal static void HideBotToken(HttpClientTraceInstrumentationOptions options)
    {
        var filter = options.FilterHttpRequestMessage;
        options.FilterHttpRequestMessage = request => !IsPoll(request) && (filter?.Invoke(request) ?? true);

        var enrich = options.EnrichWithHttpRequestMessage;
        options.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            enrich?.Invoke(activity, request);
            Redact(activity, request);
        };
    }

    private static bool IsPoll(HttpRequestMessage request) =>
        request.RequestUri is { } uri
        && BotPath().IsMatch(uri.AbsolutePath)
        && uri.AbsolutePath.EndsWith("/getUpdates", StringComparison.OrdinalIgnoreCase);

    private static void Redact(Activity activity, HttpRequestMessage request)
    {
        if (request.RequestUri is { } uri && BotPath().IsMatch(uri.AbsolutePath))
        {
            activity.SetTag("url.full", BotPath().Replace(uri.GetLeftPart(UriPartial.Path), "/bot{token}/"));
        }
    }

    [GeneratedRegex(@"/bot[0-9]+:[A-Za-z0-9_-]+/")]
    private static partial Regex BotPath();
}

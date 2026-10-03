using System.Text;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Search;

namespace PaperPilot.Api.Telegram;

/// <summary>The bot's fixed replies, word for word from Python's <c>services/telegram/bot.py</c> unless marked.</summary>
internal static class TelegramMessages
{
    public const string Start =
        "Welcome to arXiv Paper Curator!\n\n"
        + "Ask me questions about CS papers and I'll provide answers with sources.\n\n"
        + "Commands:\n"
        + "/help - Show this help\n"
        + "/search <keywords> - Search papers";

    public const string Help =
        "Send me any question about computer science research papers.\n\n"
        + "Examples:\n"
        + "- What are transformer architectures?\n"
        + "- How does BERT work?\n"
        + "- Explain attention mechanisms\n\n"
        + "Use /search to find specific papers.";

    public const string SearchUsage = "Usage: /search <keywords>\nExample: /search neural networks";

    public const string NoPapersFound = "No papers found. Try different keywords.";

    public const string NoRelevantPapers = "No relevant papers found. Try rephrasing your question.";

    /// <summary>New (B6): Python showed the raw error, or no papers at all.</summary>
    public const string SearchUnavailable = "Search is temporarily unavailable. Please try again later.";

    /// <summary>New (N5).</summary>
    public const string AgentUsage = "Usage: /agent <question>\nExample: /agent What are transformer architectures?";

    public static string SearchFailed(string error) => $"Search failed: {error}";

    public static string Error(string error) => $"Error: {error}";
}

/// <summary>Builds the bot's answer and search messages, and splits long ones (B19).</summary>
internal static class TelegramMessageFormatter
{
    /// <summary>Telegram's limit on a message's text.</summary>
    public const int MaxMessageLength = 4096;

    private const int MaxSources = 5;
    private const int MaxPapers = 5;
    private const string PdfUrlPrefix = "https://arxiv.org/pdf/";

    /// <summary>
    /// <c>*Answer:*</c>, the answer, then up to five sources as abstract-page links. The text is legacy Markdown.
    /// </summary>
    public static string Answer(AskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var message = new StringBuilder("*Answer:*\n").Append(response.Answer).Append('\n');
        if (response.Sources.Count > 0)
        {
            message.Append("\n*Sources:*\n");
            foreach (var (source, index) in response.Sources.Take(MaxSources).Select((s, i) => (s, i + 1)))
            {
                message.Append(index).Append(". ").Append(AbsUrl(source)).Append('\n');
            }
        }

        return message.ToString();
    }

    /// <summary>The <c>/agent</c> reply (N5): the answer as <see cref="Answer"/> formats it, then the reasoning steps.</summary>
    public static string AgentAnswer(AgenticAskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var message = new StringBuilder(Answer(response)).Append("\n*Reasoning:*\n");
        foreach (var step in response.ReasoningSteps)
        {
            message.Append("- ").Append(step).Append('\n');
        }

        return message.ToString();
    }

    /// <summary>
    /// <c>Found {n} papers:</c> and the first five distinct papers among <paramref name="hits"/>, with their
    /// abstract-page links; <see cref="TelegramMessages.NoPapersFound"/> when there are no hits.
    /// </summary>
    public static string SearchResults(IReadOnlyList<ChunkHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return TelegramMessages.NoPapersFound;
        }

        var papers = hits.Where(h => !string.IsNullOrEmpty(h.ArxivId)).DistinctBy(h => h.ArxivId).Take(MaxPapers).ToList();
        var message = new StringBuilder($"Found {papers.Count} papers:\n\n");
        foreach (var (paper, index) in papers.Select((p, i) => (p, i + 1)))
        {
            message.Append(index).Append(". ").Append(paper.Title ?? "Untitled").Append('\n')
                .Append(ArxivId.ToAbsUrl(paper.ArxivId)).Append("\n\n");
        }

        return message.ToString();
    }

    /// <summary>
    /// Splits <paramref name="text"/> into messages of at most <paramref name="maxLength"/> characters (B19): at the last
    /// paragraph break that fits, else the last line break, else a hard cut that keeps surrogate pairs whole. The
    /// break itself is dropped, and blank parts are skipped (Telegram rejects empty messages).
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxLength = MaxMessageLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);

        List<string> parts = [];
        var rest = text;
        while (rest.Length > maxLength)
        {
            var window = rest[..maxLength];
            int cut;
            int skip;
            if (window.LastIndexOf("\n\n", StringComparison.Ordinal) is > 0 and var paragraph)
            {
                (cut, skip) = (paragraph, 2);
            }
            else if (window.LastIndexOf('\n') is > 0 and var line)
            {
                (cut, skip) = (line, 1);
            }
            else
            {
                (cut, skip) = (char.IsHighSurrogate(window[^1]) ? maxLength - 1 : maxLength, 0);
            }

            Add(rest[..cut]);
            rest = rest[(cut + skip)..];
        }

        Add(rest);
        return parts;

        void Add(string part)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                parts.Add(part);
            }
        }
    }

    /// <summary>The abstract page for a source from <see cref="ArxivId.ToPdfUrl"/>.</summary>
    internal static string AbsUrl(string pdfUrl)
    {
        var id = pdfUrl.StartsWith(PdfUrlPrefix, StringComparison.Ordinal)
            ? pdfUrl[PdfUrlPrefix.Length..]
            : pdfUrl[(pdfUrl.LastIndexOf('/') + 1)..];
        if (id.EndsWith(".pdf", StringComparison.Ordinal))
        {
            id = id[..^".pdf".Length];
        }

        return ArxivId.ToAbsUrl(id);
    }
}

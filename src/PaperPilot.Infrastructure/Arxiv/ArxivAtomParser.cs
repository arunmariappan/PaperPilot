using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaperPilot.Core.Domain;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Text;

namespace PaperPilot.Infrastructure.Arxiv;

/// <summary>Parses arXiv API Atom feeds, as Python's <c>ArxivClient._parse_response</c> did.</summary>
public static partial class ArxivAtomParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// The feed's entries. An entry that can't be parsed (no id, an unreadable date) is skipped and logged.
    /// </summary>
    /// <exception cref="ArxivApiException">The XML itself is malformed.</exception>
    public static IReadOnlyList<ArxivPaper> Parse(string xml, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new ArxivApiException($"Failed to parse arXiv XML response: {ex.Message}", ex);
        }

        var papers = new List<ArxivPaper>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            try
            {
                if (ParseEntry(entry) is { } paper)
                {
                    papers.Add(paper);
                }
            }
            catch (FormatException ex)
            {
                LogEntrySkipped(logger, ex.Message);
            }
        }

        return papers;
    }

    private static ArxivPaper? ParseEntry(XElement entry)
    {
        var id = entry.Element(Atom + "id")?.Value;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var published = Text(entry, "published");
        return new ArxivPaper(
            ArxivId: id.Split('/')[^1],
            Title: Text(entry, "title").Replace('\n', ' '),
            Authors: [.. entry.Elements(Atom + "author").Select(a => Text(a, "name")).Where(name => name.Length > 0)],
            Abstract: Text(entry, "summary").Replace('\n', ' '),
            Categories: [.. entry.Elements(Atom + "category").Select(c => (string?)c.Attribute("term")).OfType<string>().Where(t => t.Length > 0)],
            PublishedDate: DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? date
                : throw new FormatException($"Entry {id} has an unreadable published date '{published}'"),
            PdfUrl: PdfUrl(entry));
    }

    /// <summary>The <c>application/pdf</c> link, always https.</summary>
    private static string PdfUrl(XElement entry)
    {
        var href = entry.Elements(Atom + "link").FirstOrDefault(l => (string?)l.Attribute("type") == "application/pdf")
            ?.Attribute("href")?.Value ?? string.Empty;
        return href.StartsWith("http://arxiv.org/", StringComparison.Ordinal)
            ? "https://arxiv.org/" + href["http://arxiv.org/".Length..]
            : href;
    }

    private static string Text(XElement element, string name) =>
        element.Element(Atom + name) is { } child ? PythonText.Strip(child.Value) : string.Empty;

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to parse entry: {Reason}")]
    private static partial void LogEntrySkipped(ILogger logger, string reason);
}

using System.Globalization;
using System.Text;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Arxiv;

/// <summary>arXiv API URLs, encoded exactly as Python's <c>urlencode(..., quote_via=quote, safe=...)</c> encodes them.</summary>
public static class ArxivQueryBuilder
{
    /// <summary>The arXiv API returns at most this many results per query.</summary>
    public const int MaxResultsPerQuery = 2000;

    /// <summary>
    /// <c>cat:{category}</c>, plus <c> AND submittedDate:[{from}0000+TO+{to}2359]</c> when either date is given (a missing
    /// one becomes <c>*</c>), newest submissions first.
    /// </summary>
    public static string BuildSearchUrl(
        string baseUrl, string category, int maxResults, DateOnly? from = null, DateOnly? to = null, int start = 0)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(category);

        var query = $"cat:{category}";
        if (from is not null || to is not null)
        {
            var fromPart = from is { } f ? f.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "0000" : "*";
            var toPart = to is { } t ? t.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "2359" : "*";
            query += $" AND submittedDate:[{fromPart}+TO+{toPart}]";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{baseUrl}?search_query={Quote(query, ":+[]")}&start={start}"
            + $"&max_results={Math.Min(maxResults, MaxResultsPerQuery)}&sortBy=submittedDate&sortOrder=descending");
    }

    /// <summary>A lookup by id. The version suffix is stripped (B8), so arXiv returns the latest version.</summary>
    public static string BuildIdUrl(string baseUrl, string arxivId)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return $"{baseUrl}?id_list={Quote(ArxivId.StripVersion(arxivId), ":+[]*")}&max_results=1";
    }

    /// <summary>
    /// Python's <c>urllib.parse.quote</c>: letters, digits, <c>_.-~</c> and the <paramref name="safe"/> characters are
    /// kept; every other UTF-8 byte becomes <c>%XX</c>.
    /// </summary>
    internal static string Quote(string value, string safe)
    {
        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || (b < 0x80 && safe.Contains(c, StringComparison.Ordinal)))
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append(CultureInfo.InvariantCulture, $"%{b:X2}");
            }
        }

        return encoded.ToString();
    }
}

using System.Globalization;

namespace PaperPilot.Core.Text;

/// <summary>
/// Python <c>str</c> semantics that ported algorithms depend on, so their output (word counts, character offsets)
/// matches the Python version exactly.
/// </summary>
public static class PythonText
{
    /// <summary><c>str.isspace()</c>: .NET whitespace plus the information separators U+001C–U+001F.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    /// <summary><c>str.split()</c> with no arguments: words separated by any run of whitespace.</summary>
    public static string[] Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var words = new List<string>();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsSpace(text[i]))
            {
                if (start >= 0)
                {
                    words.Add(text[start..i]);
                    start = -1;
                }
            }
            else if (start < 0)
            {
                start = i;
            }
        }

        if (start >= 0)
        {
            words.Add(text[start..]);
        }

        return [.. words];
    }

    /// <summary><c>str.strip()</c>.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary><c>len(str)</c>: code points, so a character outside the BMP (such as 𝑥) counts once.</summary>
    public static int Length(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var surrogatePairs = 0;
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (char.IsSurrogatePair(text[i], text[i + 1]))
            {
                surrogatePairs++;
                i++;
            }
        }

        return text.Length - surrogatePairs;
    }

    /// <summary><c>str.lower()</c>, close enough for the ASCII keywords it is compared against.</summary>
    public static string Lower(string text) => text.ToLower(CultureInfo.InvariantCulture);
}

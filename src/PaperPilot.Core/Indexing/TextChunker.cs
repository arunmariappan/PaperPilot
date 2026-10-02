using PaperPilot.Core.Domain;
using PaperPilot.Core.Options;
using PaperPilot.Core.Text;

namespace PaperPilot.Core.Indexing;

/// <summary>A chunk of a paper, ready to be embedded and indexed.</summary>
/// <param name="StartChar">Character offset (in code points, like Python) of the chunk within the text it was cut from.</param>
/// <param name="SectionTitle">The section, a combination like <c>A + B</c>, or <c>Title (Part n)</c>; null for plain word windows.</param>
public sealed record TextChunk(string Text, int ChunkIndex, int StartChar, int EndChar, int WordCount, string? SectionTitle);

/// <summary>
/// Port of Python's <c>TextChunker</c>: section-based chunking with a word-window fallback. Differences from Python:
/// short texts give one chunk instead of crashing (B9), combined sections are joined with real newlines (B10), sizes
/// come from <see cref="ChunkingOptions"/> (B11), and sections that share a title are all kept (B31).
/// </summary>
public sealed class TextChunker(ChunkingOptions options)
{
    /// <summary>A buffer of small sections with fewer words than this (header included) is merged into the previous chunk.</summary>
    private const int MergeThresholdWords = 200;

    private static readonly string[] MetadataTitles =
        ["content", "header", "authors", "author", "affiliation", "email", "arxiv", "preprint", "submitted", "received", "accepted"];

    private static readonly string[] MetadataPatterns =
        ["@", "arxiv:", "university", "institute", "department", "college", "gmail.com", "edu", "ac.uk", "preprint"];

    /// <summary>
    /// Chunks a paper by section when <c>Chunking:SectionBased</c> is on and some sections survive filtering;
    /// otherwise chunks <paramref name="fullText"/> into word windows. Returns no chunks for a paper without text.
    /// </summary>
    public IReadOnlyList<TextChunk> ChunkPaper(
        string title, string @abstract, string? fullText, IReadOnlyList<PaperSection>? sections)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(@abstract);

        if (options.SectionBased && sections is { Count: > 0 })
        {
            var chunks = ChunkBySections(title, @abstract, sections);
            if (chunks.Count > 0)
            {
                return chunks;
            }
        }

        return ChunkText(fullText);
    }

    /// <summary>
    /// Windows of <c>ChunkSize</c> words that overlap by <c>OverlapSize</c>. A text under <c>MinChunkSize</c> words is
    /// one chunk. Offsets are lengths of the space-joined words before the window, as in Python.
    /// </summary>
    public IReadOnlyList<TextChunk> ChunkText(string? text)
    {
        if (text is null || PythonText.Strip(text).Length == 0)
        {
            return [];
        }

        var words = PythonText.Split(text);
        if (words.Length < options.MinChunkSize)
        {
            // Python crashed here (B9).
            return [new TextChunk(string.Join(' ', words), 0, 0, PythonText.Length(text), words.Length, null)];
        }

        // joined[k] = length of the first k words joined by single spaces.
        var joined = new int[words.Length + 1];
        for (var k = 1; k <= words.Length; k++)
        {
            joined[k] = joined[k - 1] + PythonText.Length(words[k - 1]) + (k > 1 ? 1 : 0);
        }

        var chunks = new List<TextChunk>();
        for (var position = 0; position < words.Length; position += options.ChunkSize - options.OverlapSize)
        {
            var end = Math.Min(position + options.ChunkSize, words.Length);
            chunks.Add(new TextChunk(
                string.Join(' ', words[position..end]),
                chunks.Count,
                joined[position],
                joined[end],
                end - position,
                null));

            if (end >= words.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private List<TextChunk> ChunkBySections(string title, string @abstract, IReadOnlyList<PaperSection> sections)
    {
        var kept = FilterSections(sections, @abstract);
        var header = $"{title}\n\nAbstract: {@abstract}\n\n";

        var chunks = new List<TextChunk>();
        var small = new List<(string Title, string Content, int Words)>();
        for (var i = 0; i < kept.Count; i++)
        {
            var (sectionTitle, content) = kept[i];
            var words = PythonText.Split(content).Length;

            if (words < options.SectionMinWords)
            {
                small.Add((sectionTitle, content, words));
                var isLast = i == kept.Count - 1;
                if (isLast || PythonText.Split(kept[i + 1].Content).Length >= options.SectionMinWords)
                {
                    AddCombined(header, small, chunks);
                    small.Clear();
                }
            }
            else if (words <= options.SectionMaxWords)
            {
                chunks.Add(SectionChunk($"{header}Section: {sectionTitle}\n\n{content}", sectionTitle, chunks.Count));
            }
            else
            {
                var parts = ChunkText($"Section: {sectionTitle}\n\n{content}");
                var headerLength = PythonText.Length(header);
                for (var part = 0; part < parts.Count; part++)
                {
                    var text = header + parts[part].Text;
                    chunks.Add(new TextChunk(
                        text,
                        chunks.Count,
                        parts[part].StartChar,
                        parts[part].EndChar + headerLength,
                        PythonText.Split(text).Length,
                        $"{sectionTitle} (Part {part + 1})"));
                }
            }
        }

        return chunks;
    }

    /// <summary>
    /// Adds a buffer of small sections as one chunk, or appends it to the previous chunk when it is still short.
    /// </summary>
    private static void AddCombined(string header, List<(string Title, string Content, int Words)> small, List<TextChunk> chunks)
    {
        if (small.Count == 0)
        {
            return;
        }

        // Python joined these with a literal backslash-n instead of newlines (B10).
        var combined = string.Join("\n\n", small.Select(s => $"Section: {s.Title}\n\n{s.Content}"));
        var totalWords = small.Sum(s => s.Words);

        if (totalWords + PythonText.Split(header).Length < MergeThresholdWords && chunks.Count > 0)
        {
            var previous = chunks[^1];
            var merged = $"{previous.Text}\n\n{combined}";
            chunks[^1] = new TextChunk(
                merged,
                previous.ChunkIndex,
                0,
                PythonText.Length(merged),
                PythonText.Split(merged).Length,
                $"{previous.SectionTitle} + Combined");
            return;
        }

        var title = string.Join(" + ", small.Take(3).Select(s => s.Title));
        if (small.Count > 3)
        {
            title += $" + {small.Count - 3} more";
        }

        chunks.Add(SectionChunk(header + combined, title, chunks.Count));
    }

    private static TextChunk SectionChunk(string text, string title, int index) =>
        new(text, index, 0, PythonText.Length(text), PythonText.Split(text).Length, title);

    /// <summary>
    /// Drops empty sections, metadata sections (by title or content) and sections that repeat the abstract.
    /// Content is stripped. Unlike Python's dict, sections that share a title are all kept (B31).
    /// </summary>
    private static List<(string Title, string Content)> FilterSections(IReadOnlyList<PaperSection> sections, string @abstract)
    {
        var abstractLower = PythonText.Strip(PythonText.Lower(@abstract));
        var abstractWords = PythonText.Split(PythonText.Lower(@abstract)).ToHashSet(StringComparer.Ordinal);

        var kept = new List<(string, string)>();
        foreach (var section in sections)
        {
            var content = PythonText.Strip(section.Content ?? string.Empty);
            if (content.Length == 0
                || IsMetadataTitle(section.Title)
                || IsAbstractDuplicate(content, abstractLower, abstractWords)
                || (PythonText.Split(content).Length < 20 && IsMetadataContent(content)))
            {
                continue;
            }

            kept.Add((section.Title, content));
        }

        return kept;
    }

    private static bool IsMetadataTitle(string title)
    {
        var lower = PythonText.Strip(PythonText.Lower(title));
        var length = PythonText.Length(lower);
        return MetadataTitles.Contains(lower)
            || length < 5
            || (length < 20 && MetadataTitles.Any(indicator => lower.Contains(indicator, StringComparison.Ordinal)));
    }

    private static bool IsAbstractDuplicate(string content, string abstractLower, HashSet<string> abstractWords)
    {
        var contentLower = PythonText.Strip(PythonText.Lower(content));
        if (contentLower.Contains(abstractLower, StringComparison.Ordinal)
            || abstractLower.Contains(contentLower, StringComparison.Ordinal))
        {
            return true;
        }

        if (abstractWords.Count <= 10)
        {
            return false;
        }

        var overlap = abstractWords.Intersect(PythonText.Split(contentLower), StringComparer.Ordinal).Count();
        return (double)overlap / abstractWords.Count > 0.8;
    }

    private static bool IsMetadataContent(string content)
    {
        if (PythonText.Split(content).Length >= 30)
        {
            return false;
        }

        var lower = PythonText.Lower(content);
        return MetadataPatterns.Count(pattern => lower.Contains(pattern, StringComparison.Ordinal)) >= 2;
    }
}

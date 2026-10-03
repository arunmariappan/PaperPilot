using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PaperPilot.Web.Chat;

/// <summary>
/// Renders model output as HTML. The text comes from an LLM that has read untrusted paper text, so it must never inject
/// markup or script: raw HTML is shown as text, links keep only http(s) and mailto URLs, and images become links.
/// </summary>
public static class AnswerMarkdown
{
    private static readonly string[] SafeSchemes = [Uri.UriSchemeHttp, Uri.UriSchemeHttps, Uri.UriSchemeMailto];

    // No UseAdvancedExtensions: its generic attributes ({onclick=...}) would let the text add any attribute.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseListExtras()
        .UsePipeTables()
        .DisableHtml()
        .Build();

    public static string ToHtml(string markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (!IsSafe(link.Url))
            {
                Unwrap(link);
                continue;
            }

            link.IsImage = false;
            OpenInNewTab(link);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (autolink.IsEmail || IsSafe(autolink.Url))
            {
                OpenInNewTab(autolink);
            }
            else
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        return writer.ToString();
    }

    private static bool IsSafe(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && SafeSchemes.Contains(uri.Scheme, StringComparer.Ordinal);

    /// <summary>Keeps the link's text and drops the link.</summary>
    private static void Unwrap(LinkInline link)
    {
        var child = link.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            child.Remove();
            link.InsertBefore(child);
            child = next;
        }

        link.Remove();
    }

    private static void OpenInNewTab(Inline link)
    {
        var attributes = link.GetAttributes();
        attributes.AddPropertyIfNotExist("target", "_blank");
        attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
    }
}

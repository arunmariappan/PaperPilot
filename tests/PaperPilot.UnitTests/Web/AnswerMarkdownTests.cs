using PaperPilot.Web.Chat;

namespace PaperPilot.UnitTests.Web;

/// <summary>Answers are LLM output that has read untrusted paper text, so they must not inject markup or script.</summary>
public sealed class AnswerMarkdownTests
{
    [Fact]
    public void Markdown_is_rendered()
    {
        var html = AnswerMarkdown.ToHtml("**Transformers** use attention [arXiv:2610.00820].\n\n- one\n- two");

        html.ShouldContain("<strong>Transformers</strong>");
        html.ShouldContain("<li>one</li>");
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("Hi <img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://example.org\"></iframe>")]
    public void Raw_html_is_shown_as_text(string markdown)
    {
        var html = AnswerMarkdown.ToHtml(markdown);

        html.ShouldNotContain("<script");
        html.ShouldNotContain("<img");
        html.ShouldNotContain("<iframe");
        html.ShouldContain("&lt;");
    }

    [Theory]
    [InlineData("[click me](javascript:alert(1))")]
    [InlineData("[click me](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("[click me](vbscript:msgbox)")]
    public void Links_with_other_schemes_keep_only_their_text(string markdown)
    {
        var html = AnswerMarkdown.ToHtml(markdown);

        html.ShouldNotContain("<a");
        html.ShouldContain("click me");
    }

    [Fact]
    public void An_autolink_with_a_script_scheme_is_plain_text()
    {
        var html = AnswerMarkdown.ToHtml("<javascript:alert(1)>");

        html.ShouldNotContain("<a");
        html.ShouldContain("javascript:alert(1)");
    }

    [Fact]
    public void Web_links_open_in_a_new_tab_and_images_become_links()
    {
        AnswerMarkdown.ToHtml("[paper](https://arxiv.org/abs/2610.00820)").ShouldContain(
            "<a href=\"https://arxiv.org/abs/2610.00820\" target=\"_blank\" rel=\"noopener noreferrer\">paper</a>");

        var image = AnswerMarkdown.ToHtml("![chart](https://example.org/tracker.png)");
        image.ShouldNotContain("<img");
        image.ShouldContain("href=\"https://example.org/tracker.png\"");
    }

    [Fact]
    public void Attribute_syntax_is_not_enabled()
    {
        var html = AnswerMarkdown.ToHtml("# Title {onclick=alert(1)}\n\n[x](https://example.org){onmouseover=alert(1)}");

        html.ShouldNotContain("onclick=\"");
        html.ShouldNotContain("onmouseover=\"");
    }
}

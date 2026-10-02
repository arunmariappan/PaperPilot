#:package PdfPig
#:property PublishAot=false

// Writes sample-paper.pdf: a two-page, paper-shaped PDF with made-up content, so the Docling fixtures contain no
// third-party text. Regenerate with: dotnet run tests/fixtures/docling/make-sample-paper.cs
// Then convert it with docling-serve (sample-paper.json) and Python Docling (docs/plan/phase-4-ingestion.md, 4.3).

using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

const double Margin = 72;
const double LineHeight = 13;
const int CharsPerLine = 92;

var builder = new PdfDocumentBuilder();
var body = builder.AddStandard14Font(Standard14Font.TimesRoman);
var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);

var page = builder.AddPage(PageSize.A4);
var y = page.PageSize.Height - Margin;

void Line(string text, PdfDocumentBuilder.AddedFont font, double size, double x = Margin)
{
    if (y < Margin)
    {
        page = builder.AddPage(PageSize.A4);
        y = page.PageSize.Height - Margin;
    }

    page.AddText(text, size, new PdfPoint(x, y), font);
    y -= size + 4;
}

void Heading(string text)
{
    y -= 8;
    Line(text, bold, 12);
    y -= 2;
}

void Paragraph(string text)
{
    var line = "";
    foreach (var word in text.Split(' '))
    {
        if (line.Length + word.Length + 1 > CharsPerLine)
        {
            Line(line, body, 10);
            line = "";
        }

        line = line.Length == 0 ? word : $"{line} {word}";
    }

    Line(line, body, 10);
    y -= LineHeight / 2;
}

Line("Chunking Long Documents for Retrieval-Augmented Generation", bold, 16);
y -= 4;
Line("A. Example and B. Sample", body, 11);
Line("Example Institute of Technology", body, 10);
y -= 10;

Heading("Abstract");
Paragraph("Retrieval-augmented generation answers questions from a collection of documents, but long documents must first "
    + "be split into passages. We compare fixed-size word windows with section-aware chunking on a set of synthetic "
    + "papers and find that section-aware chunks keep related sentences together, which improves answer grounding.");

Heading("1 Introduction");
Paragraph("Large language models answer questions fluently, yet they cannot cite sources they have never seen. "
    + "Retrieval-augmented generation addresses this by searching a document collection and passing the most relevant "
    + "passages to the model. The quality of those passages depends on how the documents were split.");
Paragraph("This paper studies that split. We describe a hybrid strategy that keeps medium-sized sections whole, merges "
    + "very short sections with their neighbours, and divides very long sections into overlapping windows.");

Heading("2 Related Work");
Paragraph("Earlier systems used fixed windows of a few hundred words with a small overlap between consecutive windows. "
    + "Other work splits on sentence boundaries or uses the layout of the source document to find headings.");

Heading("3 Method");
Paragraph("Each document is first converted into a sequence of text items, each labelled as a title, a section header "
    + "or ordinary text. Section headers start new sections; all other text is appended to the current section.");

Heading("3.1 Window Size");
Paragraph("Sections longer than eight hundred words are split into windows of six hundred words that overlap by one "
    + "hundred words. Every window repeats the paper title and abstract so that it can be understood on its own.");

Heading("4 Experiments");
Paragraph("We built two hundred synthetic papers and asked one question per paper. Section-aware chunks answered "
    + "more questions correctly than fixed windows, and the answers cited the right section more often.");
Paragraph("The gains were largest for papers with many short sections, where fixed windows mixed unrelated topics.");

Heading("5 Conclusion");
Paragraph("Splitting documents along their own structure is a simple change that makes retrieved passages more "
    + "coherent. Future work will look at tables and figures, which this study ignored.");

Heading("References");
Paragraph("[1] A. Example. Passage retrieval for question answering. Journal of Examples, 2024.");
Paragraph("[2] B. Sample. Layout-aware document conversion. Proceedings of Samples, 2025.");

var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args.Length > 0 ? args[0] : "tests/fixtures/docling/x"))!, "sample-paper.pdf");
File.WriteAllBytes(path, builder.Build());
Console.WriteLine($"Wrote {path}");

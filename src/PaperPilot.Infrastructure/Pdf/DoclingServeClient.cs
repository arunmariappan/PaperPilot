using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Exceptions;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Pdf;

/// <summary>One item of a converted document's <c>texts[]</c>.</summary>
/// <param name="Label">For example <c>title</c>, <c>section_header</c>, <c>text</c> or <c>list_item</c>.</param>
public sealed record DoclingText(string? Label, string? Text);

/// <summary>The parts of a docling-serve conversion PaperPilot uses (plan R3); everything else is ignored.</summary>
/// <param name="TextContent">The plain-text export of the whole document.</param>
public sealed record DoclingConversion(string Status, IReadOnlyList<DoclingText> Texts, string TextContent);

/// <summary>Converts PDFs with the docling-serve sidecar (<c>POST /v1/convert/file</c>).</summary>
public sealed class DoclingServeClient(HttpClient http, IOptions<DoclingOptions> docling, IOptions<PdfParserOptions> pdf)
{
    /// <summary>
    /// Converts a PDF to Docling JSON and plain text: OCR per <c>PdfParser:DoOcr</c> (off), tables per
    /// <c>PdfParser:DoTableStructure</c> (on), image placeholders instead of base64 images, at most <c>MaxPages</c> pages.
    /// </summary>
    /// <exception cref="PdfParsingException">docling-serve failed, couldn't be reached, or reported a failed conversion.</exception>
    public async Task<DoclingConversion> ConvertAsync(string path, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, cancellationToken));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "files", Path.GetFileName(path));
        Field(form, "to_formats", "json");
        Field(form, "to_formats", "text");
        Field(form, "do_ocr", pdf.Value.DoOcr ? "true" : "false");
        Field(form, "do_table_structure", pdf.Value.DoTableStructure ? "true" : "false");
        Field(form, "image_export_mode", "placeholder");
        Field(form, "page_range", "1");
        Field(form, "page_range", pdf.Value.MaxPages.ToString(CultureInfo.InvariantCulture));
        Field(form, "pdf_backend", docling.Value.PdfBackend);

        JsonElement body;
        try
        {
            using var response = await http.PostAsync(new Uri("v1/convert/file", UriKind.Relative), form, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new PdfParsingException($"docling-serve returned {(int)response.StatusCode}: {Truncate(detail)}");
            }

            body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            or OperationCanceledException { InnerException: TimeoutException } or Polly.Timeout.TimeoutRejectedException
            && !cancellationToken.IsCancellationRequested)
        {
            throw new PdfParsingException($"docling-serve failed: {ex.Message}", ex);
        }

        return Read(body);
    }

    /// <summary>True when docling-serve answers <c>GET /health</c> within 10 seconds.</summary>
    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await http.GetAsync(new Uri("health", UriKind.Relative), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or Polly.Timeout.TimeoutRejectedException
            && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary><c>status</c> must be <c>success</c> or <c>partial_success</c>.</summary>
    public static DoclingConversion Read(JsonElement body)
    {
        var status = body.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
        if (status is not ("success" or "partial_success"))
        {
            var errors = body.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                ? string.Join("; ", e.EnumerateArray().Select(error =>
                    error.TryGetProperty("error_message", out var m) ? m.GetString() : error.ToString()))
                : "";
            throw new PdfParsingException($"Docling conversion {(status.Length > 0 ? status : "failed")}: {errors}");
        }

        var document = body.GetProperty("document");
        var texts = new List<DoclingText>();
        if (document.TryGetProperty("json_content", out var json) && json.ValueKind == JsonValueKind.Object
            && json.TryGetProperty("texts", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                texts.Add(new DoclingText(String(item, "label"), String(item, "text")));
            }
        }

        return new DoclingConversion(status, texts, String(document, "text_content") ?? "");
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Field(MultipartFormDataContent form, string name, string value) => form.Add(new StringContent(value), name);

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "…";
}

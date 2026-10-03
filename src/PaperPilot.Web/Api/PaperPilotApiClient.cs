using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PaperPilot.Core.Contracts;

namespace PaperPilot.Web.Api;

/// <summary>Whether <c>/feedback</c> stored the score.</summary>
public enum FeedbackResult
{
    Recorded,

    /// <summary>Langfuse is off (503), so there is nowhere to store feedback.</summary>
    Unavailable,
}

/// <summary>
/// The UI's only way to the API: plain HTTP, found through Aspire service discovery (<c>https+http://api</c>). Failures
/// are thrown as <see cref="ApiException"/>s with a message for the user.
/// </summary>
public sealed class PaperPilotApiClient(HttpClient http)
{
    /// <summary>
    /// Streams an answer from <c>/stream</c>. A failure after the stream started arrives as a <see cref="StreamError"/>.
    /// </summary>
    public async IAsyncEnumerable<StreamEvent> StreamAskAsync(
        AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = Post("api/v1/stream", request);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // Headers only: the body is read as the events arrive.
        using var response = await SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var streamEvent in StreamEventReader.ReadAsync(body, cancellationToken))
        {
            yield return streamEvent;
        }
    }

    /// <summary>Runs the agentic workflow (<c>/ask-agentic</c>), which can take minutes.</summary>
    public async Task<AgenticAskResponse> AskAgenticAsync(AskRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Post("api/v1/ask-agentic", request);
        using var response = await SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        return await ReadAsync<AgenticAskResponse>(response, cancellationToken);
    }

    /// <summary>Scores an answer's trace (+1 / −1) in Langfuse through <c>/feedback</c>.</summary>
    public async Task<FeedbackResult> SubmitFeedbackAsync(FeedbackRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Post("api/v1/feedback", request);
        try
        {
            using var response = await SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
            return FeedbackResult.Recorded;
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return FeedbackResult.Unavailable;
        }
    }

    /// <summary>The installed Ollama models and the API's default (<c>/models</c>).</summary>
    public async Task<ModelsResponse> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "api/v1/models");
        using var response = await SendAsync(message, HttpCompletionOption.ResponseContentRead, cancellationToken);
        return await ReadAsync<ModelsResponse>(response, cancellationToken);
    }

    private static HttpRequestMessage Post<T>(string path, T body) =>
        new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: ApiJson.Options) };

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, completion, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"Cannot reach the PaperPilot API: {ex.Message}", statusCode: null, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ApiException("The PaperPilot API did not answer in time.", statusCode: null, ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var reason = await ProblemReasonAsync(response, cancellationToken)
                ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
            throw new ApiException(reason, response.StatusCode);
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, cancellationToken)
            ?? throw new ApiException("The PaperPilot API sent an empty response.", response.StatusCode);

    /// <summary>A problem response's <c>detail</c>, its first validation error, or its <c>title</c>.</summary>
    private static async Task<string?> ProblemReasonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = problem.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var first = errors.EnumerateObject()
                    .Where(e => e.Value.ValueKind == JsonValueKind.Array)
                    .SelectMany(e => e.Value.EnumerateArray())
                    .Select(m => m.GetString())
                    .FirstOrDefault(m => !string.IsNullOrEmpty(m));
                if (first is not null)
                {
                    return first;
                }
            }

            return root.TryGetProperty("title", out var title) ? title.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

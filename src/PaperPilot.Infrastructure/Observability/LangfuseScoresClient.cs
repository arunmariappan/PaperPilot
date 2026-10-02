using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Observability;

/// <summary>Posts user feedback to Langfuse as a score on a trace (<c>POST /api/public/scores</c>).</summary>
public sealed class LangfuseScoresClient(HttpClient http, IOptions<LangfuseOptions> options)
{
    /// <summary>The score name Python used.</summary>
    public const string ScoreName = "user-feedback";

    /// <summary>True when <c>Langfuse:Enabled</c> is on and both API keys are set.</summary>
    public bool IsEnabled =>
        options.Value.Enabled
        && !string.IsNullOrWhiteSpace(options.Value.PublicKey)
        && !string.IsNullOrWhiteSpace(options.Value.SecretKey);

    /// <summary>
    /// Records <paramref name="score"/> (−1..1) on the trace. The score gets a new id, so a retried request
    /// updates the same score instead of adding a second one.
    /// </summary>
    /// <exception cref="InvalidOperationException">Langfuse is disabled.</exception>
    /// <exception cref="LangfuseException">Langfuse rejected the score or couldn't be reached.</exception>
    public async Task SubmitAsync(string traceId, double score, string? comment, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            throw new InvalidOperationException("Langfuse is disabled.");
        }

        var body = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["traceId"] = traceId,
            ["name"] = ScoreName,
            ["value"] = score,
            ["dataType"] = "NUMERIC",
        };
        if (comment is not null)
        {
            body["comment"] = comment;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/public/scores") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Value.PublicKey}:{options.Value.SecretKey}")));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException
            or TaskCanceledException { CancellationToken.IsCancellationRequested: false })
        {
            throw new LangfuseException($"Cannot reach Langfuse: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new LangfuseException($"Langfuse returned {(int)response.StatusCode}: {detail}");
            }
        }
    }
}

public sealed class LangfuseException : Exception
{
    public LangfuseException()
    {
    }

    public LangfuseException(string message)
        : base(message)
    {
    }

    public LangfuseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class LangfuseRegistration
{
    /// <summary>Registers <see cref="LangfuseScoresClient"/> on <c>Langfuse:BaseUrl</c>.</summary>
    public static IHostApplicationBuilder AddPaperPilotLangfuseScores(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddHttpClient<LangfuseScoresClient>((services, http) =>
            http.BaseAddress = new Uri(services.GetRequiredService<IOptions<LangfuseOptions>>().Value.BaseUrl.TrimEnd('/') + "/"));

        return builder;
    }
}

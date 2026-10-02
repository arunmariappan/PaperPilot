using System.Data.Common;
using Microsoft.Extensions.Configuration;

namespace PaperPilot.Infrastructure.Llm;

internal static class OllamaEndpoint
{
    public const string ConnectionName = "ollama";

    /// <summary>
    /// The <c>Endpoint</c> from the <c>ollama</c> connection string (<c>Endpoint=http://localhost:11434</c>, plus
    /// <c>;Model=...</c> when the AppHost runs the Ollama container), with a trailing slash. Null when it isn't set.
    /// </summary>
    public static Uri? FromConfiguration(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var parts = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var endpoint = parts.TryGetValue("Endpoint", out var value) ? value?.ToString() : connectionString;
        return Uri.TryCreate(endpoint?.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ? uri : null;
    }
}

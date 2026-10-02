using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PaperPilot.Core.Contracts;

namespace PaperPilot.Core.Caching;

/// <summary>Redis keys for cached <c>/ask</c> answers.</summary>
public static class CacheKey
{
    /// <summary>PaperPilot's own prefix; Python used <c>exact_cache:</c> (C6).</summary>
    public const string Prefix = "paperpilot:ask:";

    /// <summary>
    /// <see cref="Prefix"/> plus the first 16 hex characters of the SHA-256 of the request's canonical JSON:
    /// <c>{"categories": [...], "model": ..., "query": ..., "top_k": ..., "use_hybrid": ...}</c>, sorted keys, sorted
    /// categories, written exactly as Python's <c>json.dumps(..., sort_keys=True)</c> writes it. The hash part therefore
    /// equals Python's for the same request.
    /// </summary>
    /// <param name="request">The question and its retrieval settings.</param>
    /// <param name="model">The model that answers, after defaulting (<c>request.Model ?? Ollama:Model</c>).</param>
    public static string Compute(AskRequest request, string model)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);

        var json = new StringBuilder("{\"categories\": [");
        var categories = (request.Categories ?? []).Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < categories.Count; i++)
        {
            json.Append(i == 0 ? "" : ", ");
            AppendPythonString(json, categories[i]);
        }

        json.Append("], \"model\": ");
        AppendPythonString(json, model);
        json.Append(", \"query\": ");
        AppendPythonString(json, request.Query);
        json.Append(CultureInfo.InvariantCulture, $", \"top_k\": {request.TopK}, \"use_hybrid\": ");
        json.Append(request.UseHybrid ? "true" : "false").Append('}');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()));
        return Prefix + Convert.ToHexStringLower(hash)[..16];
    }

    /// <summary>A JSON string as Python writes it with <c>ensure_ascii=True</c>: everything outside space..~ escaped.</summary>
    private static void AppendPythonString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\n': json.Append("\\n"); break;
                case '\r': json.Append("\\r"); break;
                case '\t': json.Append("\\t"); break;
                case '\b': json.Append("\\b"); break;
                case '\f': json.Append("\\f"); break;
                case >= ' ' and <= '~': json.Append(c); break;
                default: json.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"); break;
            }
        }

        json.Append('"');
    }
}

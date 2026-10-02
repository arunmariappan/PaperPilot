using System.Text.Json;

namespace PaperPilot.Core.Contracts;

/// <summary>
/// The public API's wire format: snake_case names, like the Python version (<c>top_k</c>, <c>chunks_used</c>).
/// The API host gets the same settings from <c>AddServiceDefaults</c>; clients and tests use these.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

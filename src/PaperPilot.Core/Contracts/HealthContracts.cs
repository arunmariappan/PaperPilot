namespace PaperPilot.Core.Contracts;

/// <summary>Response from <c>/api/v1/health</c>.</summary>
public sealed record HealthResponse
{
    /// <summary><c>ok</c> when every dependency is healthy, otherwise <c>degraded</c>.</summary>
    public required string Status { get; init; }

    public required string Version { get; init; }

    public required string Environment { get; init; }

    public required string ServiceName { get; init; }

    /// <summary>Keyed by dependency: <c>database</c>, <c>opensearch</c>, <c>ollama</c>.</summary>
    public IReadOnlyDictionary<string, ServiceStatus>? Services { get; init; }
}

/// <summary>Health of one dependency.</summary>
/// <param name="Status"><c>healthy</c> or <c>unhealthy</c>.</param>
public sealed record ServiceStatus(string Status, string? Message = null);

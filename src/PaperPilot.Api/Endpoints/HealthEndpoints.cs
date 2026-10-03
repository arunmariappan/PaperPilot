using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Options;

namespace PaperPilot.Api.Endpoints;

internal static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/health", HealthAsync)
            .WithName("Health")
            .WithTags("Health")
            .WithSummary("Service health with database, OpenSearch and Ollama checks")
            .WithDescription("Always 200; `status` is `degraded` when any dependency is unhealthy.");

        return group;
    }

    private static async Task<Ok<HealthResponse>> HealthAsync(
        HealthCheckService healthChecks,
        IOptions<AppOptions> app,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(
            check => check.Tags.Contains(PaperPilot.Infrastructure.HealthChecks.ApiTag), cancellationToken);

        var services = report.Entries.ToDictionary(
            entry => entry.Key,
            entry => new ServiceStatus(
                entry.Value.Status == HealthStatus.Healthy ? "healthy" : "unhealthy",
                entry.Value.Description ?? entry.Value.Exception?.Message));

        return TypedResults.Ok(new HealthResponse
        {
            Status = services.Values.All(s => s.Status == "healthy") ? "ok" : "degraded",
            Version = app.Value.Version,
            Environment = environment.EnvironmentName.ToLowerInvariant(),
            ServiceName = app.Value.ServiceName,
            Services = services,
        });
    }
}

namespace PaperPilot.Web.Api;

public static class ApiClientRegistration
{
    /// <summary>
    /// Streams and agentic answers take minutes on a local model, so the standard resilience handler (~30 s in total,
    /// with retries) is removed (R1); a retry would also repeat a whole generation. Stop and closing the page cancel a
    /// request; this only catches one that hangs.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddPaperPilotApiClient(this IServiceCollection services)
    {
        var client = services.AddHttpClient<PaperPilotApiClient>(http =>
        {
            http.BaseAddress = new Uri("https+http://api");
            http.Timeout = Timeout;
        });

        // Experimental in Microsoft.Extensions.Http.Resilience 10.x, but it is the supported way to drop the default handler.
#pragma warning disable EXTEXP0001
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        return services;
    }
}

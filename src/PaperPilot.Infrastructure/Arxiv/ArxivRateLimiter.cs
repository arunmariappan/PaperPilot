using Microsoft.Extensions.Options;
using PaperPilot.Core.Options;

namespace PaperPilot.Infrastructure.Arxiv;

/// <summary>
/// One shared gate for all arXiv traffic, API queries and PDF downloads alike: a request may start only
/// <c>Arxiv:RateLimitDelaySeconds</c> after the previous one started, as arXiv asks (B26). Transfers can overlap.
/// A minimum-interval gate rather than a token bucket: a bucket refills on a fixed timer, so two requests could start
/// moments apart around a refill.
/// </summary>
public sealed class ArxivRateLimiter(IOptions<ArxivOptions> options, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastStart;

    /// <summary>Waits until the next request may start, and claims that slot.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_lastStart is { } last)
            {
                var wait = TimeSpan.FromSeconds(options.Value.RateLimitDelaySeconds) - time.GetElapsedTime(last);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, time, cancellationToken);
                }
            }

            _lastStart = time.GetTimestamp();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

/// <summary>Puts every request of the arXiv HTTP client through <see cref="ArxivRateLimiter"/>.</summary>
internal sealed class ArxivRateLimitHandler(ArxivRateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}

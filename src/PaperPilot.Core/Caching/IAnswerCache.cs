using PaperPilot.Core.Contracts;

namespace PaperPilot.Core.Caching;

/// <summary>
/// Exact-match cache of <c>/ask</c> answers, keyed by <see cref="CacheKey"/>. Implementations never throw: a cache
/// failure is logged and treated as a miss, so it can't fail a request.
/// </summary>
public interface IAnswerCache
{
    /// <summary>The cached answer for this request and model, or null on a miss or a cache failure.</summary>
    Task<AskResponse?> TryGetAsync(AskRequest request, string model, CancellationToken cancellationToken = default);

    /// <summary>Caches the answer for <c>Cache:TtlHours</c>.</summary>
    Task StoreAsync(AskRequest request, string model, AskResponse response, CancellationToken cancellationToken = default);
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaperPilot.Core.Caching;
using PaperPilot.Core.Contracts;
using PaperPilot.Core.Options;
using StackExchange.Redis;

namespace PaperPilot.Infrastructure.Caching;

/// <summary>The Redis answer cache. The value is the <see cref="AskResponse"/> JSON, as Python stored it.</summary>
internal sealed partial class AnswerCache(
    IConnectionMultiplexer redis, IOptions<CacheOptions> options, ILogger<AnswerCache> logger) : IAnswerCache
{
    public async Task<AskResponse?> TryGetAsync(AskRequest request, string model, CancellationToken cancellationToken = default)
    {
        var key = CacheKey.Compute(request, model);
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key).WaitAsync(cancellationToken);
            if (value.IsNullOrEmpty)
            {
                return null;
            }

            var response = JsonSerializer.Deserialize<AskResponse>(value.ToString(), ApiJson.Options);
            LogHit(logger, key);
            return response;
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            LogLookupFailed(logger, ex.Message);
            Activity.Current?.AddException(ex);
            return null;
        }
    }

    public async Task StoreAsync(AskRequest request, string model, AskResponse response, CancellationToken cancellationToken = default)
    {
        var key = CacheKey.Compute(request, model);
        try
        {
            var json = JsonSerializer.Serialize(response, ApiJson.Options);
            await redis.GetDatabase()
                .StringSetAsync(key, json, TimeSpan.FromHours(options.Value.TtlHours))
                .WaitAsync(cancellationToken);
            LogStored(logger, key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            LogStoreFailed(logger, ex.Message);
            Activity.Current?.AddException(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache hit for exact query match ({Key})")]
    private static partial void LogHit(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored response in exact cache with key {Key}")]
    private static partial void LogStored(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache check failed, proceeding with normal flow: {Reason}")]
    private static partial void LogLookupFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to store response in cache: {Reason}")]
    private static partial void LogStoreFailed(ILogger logger, string reason);
}

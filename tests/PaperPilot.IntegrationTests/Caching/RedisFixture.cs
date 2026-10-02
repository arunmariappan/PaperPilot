using StackExchange.Redis;
using Testcontainers.Redis;

namespace PaperPilot.IntegrationTests.Caching;

/// <summary>One Redis container (the image Aspire runs, without TLS) for the whole collection.</summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:8.6").Build();

    public string ConnectionString => _container.GetConnectionString();

    public ConnectionMultiplexer Connection { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString + ",allowAdmin=true");
    }

    /// <summary>The cached-answer keys, for asserting what was (not) cached.</summary>
    public IReadOnlyList<string> AnswerKeys() =>
        [.. Connection.GetServers().Single().Keys(pattern: "paperpilot:ask:*").Select(k => k.ToString())];

    public Task FlushAsync() => Connection.GetServers().Single().FlushAllDatabasesAsync();

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null)
        {
            await Connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

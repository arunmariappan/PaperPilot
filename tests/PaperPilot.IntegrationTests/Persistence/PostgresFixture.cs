using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using PaperPilot.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace PaperPilot.IntegrationTests.Persistence;

/// <summary>
/// Every test class that uses containers joins this collection: the containers start once, and the classes run one
/// at a time, so they never share an index or table concurrently.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ContainersCollectionDefinition
    : ICollectionFixture<PostgresFixture>, ICollectionFixture<Search.OpenSearchFixture>, ICollectionFixture<Caching.RedisFixture>
{
    public const string Name = "Containers";
}

/// <summary>
/// One Postgres container (the image Aspire runs) for the whole collection, migrated once. Services are built through
/// <see cref="PersistenceRegistration.AddPaperPilotDatabase"/>, with a fake clock.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18.3").Build();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 2, 6, 0, 0, TimeSpan.Zero));

    public IHost Host { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{PersistenceRegistration.ConnectionName}"] = _container.GetConnectionString(),
        });
        builder.Services.AddSingleton<TimeProvider>(Clock);
        builder.AddPaperPilotDatabase();
        Host = builder.Build();

        await using var scope = Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Host?.Dispose();
        await _container.DisposeAsync();
    }
}

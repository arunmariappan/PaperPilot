using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PaperPilot.Core.Domain;
using PaperPilot.Infrastructure.Persistence;

namespace PaperPilot.IntegrationTests.Persistence;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class SchemaTests(PostgresFixture postgres) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>().Papers.ExecuteDeleteAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Initial_migration_is_applied_and_nothing_is_pending()
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>().Database;

        (await database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
            .ShouldContain(m => m.EndsWith("_Initial", StringComparison.Ordinal));
        (await database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Columns_are_snake_case_with_arrays_jsonb_and_timestamptz()
    {
        var columns = await QueryAsync(
            "SELECT column_name, udt_name FROM information_schema.columns WHERE table_name = 'papers'");

        columns.ShouldBe(
            new Dictionary<string, string>
            {
                ["id"] = "uuid",
                ["arxiv_id"] = "text",
                ["title"] = "text",
                ["authors"] = "_text",
                ["abstract"] = "text",
                ["categories"] = "_text",
                ["published_date"] = "timestamptz",
                ["pdf_url"] = "text",
                ["raw_text"] = "text",
                ["sections"] = "jsonb",
                ["references"] = "_text",
                ["parser_used"] = "text",
                ["parser_metadata"] = "jsonb",
                ["pdf_processed"] = "bool",
                ["pdf_processing_date"] = "timestamptz",
                ["created_at"] = "timestamptz",
                ["updated_at"] = "timestamptz",
            },
            ignoreOrder: true);
    }

    [Fact]
    public async Task Arxiv_id_is_unique()
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>();
        db.Papers.Add(NewPaper("2510.00001v1"));
        db.Papers.Add(NewPaper("2510.00001v1"));

        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        error.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private static Paper NewPaper(string arxivId) =>
        new() { ArxivId = arxivId, Title = "T", Abstract = "A", PdfUrl = ArxivId.ToPdfUrl(arxivId) };

    private async Task<Dictionary<string, string>> QueryAsync(string sql)
    {
        await using var scope = postgres.Host.Services.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<PaperPilotDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var rows = new Dictionary<string, string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }
}

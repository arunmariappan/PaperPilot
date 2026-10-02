using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Persistence;

public sealed class PaperPilotDbContext(DbContextOptions<PaperPilotDbContext> options) : DbContext(options)
{
    public DbSet<Paper> Papers => Set<Paper>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaperPilotDbContext).Assembly);

    /// <summary>Provider settings shared by the running services and the <c>dotnet ef</c> tools.</summary>
    internal static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();
}

/// <summary>Lets <c>dotnet ef migrations</c> build the model without starting a host or reaching a database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PaperPilotDbContext>
{
    public PaperPilotDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PaperPilotDbContext>();
        PaperPilotDbContext.Configure(options, "Host=localhost;Database=papers");
        return new PaperPilotDbContext(options.Options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Persistence.Configurations;

/// <summary>The <c>ingestion_runs</c> table (N2). Target dates are <c>date</c>, errors a <c>text[]</c>.</summary>
internal sealed class IngestionRunConfiguration : IEntityTypeConfiguration<IngestionRun>
{
    public void Configure(EntityTypeBuilder<IngestionRun> builder)
    {
        builder.ToTable("ingestion_runs");
        builder.HasKey(r => r.Id);
        builder.HasIndex(r => r.StartedAt);
        builder.Property(r => r.Trigger).HasMaxLength(20);
        builder.Property(r => r.Status).HasMaxLength(20);
        builder.Property(r => r.HangfireJobId).HasMaxLength(100);
    }
}

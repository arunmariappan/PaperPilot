using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Persistence.Configurations;

/// <summary>
/// The <c>papers</c> table. Column names come from the snake_case convention. String lists are native
/// <c>text[]</c> arrays, sections and parser metadata are <c>jsonb</c>, and every timestamp is <c>timestamptz</c> (B21).
/// </summary>
internal sealed class PaperConfiguration : IEntityTypeConfiguration<Paper>
{
    public void Configure(EntityTypeBuilder<Paper> builder)
    {
        builder.ToTable("papers");
        builder.HasKey(p => p.Id);
        builder.HasIndex(p => p.ArxivId).IsUnique();

        builder.ComplexCollection(p => p.Sections, sections =>
        {
            sections.ToJson("sections");
            sections.Property(s => s.Title).HasJsonPropertyName("title");
            sections.Property(s => s.Content).HasJsonPropertyName("content");
            sections.Property(s => s.Level).HasJsonPropertyName("level");
        });
    }
}

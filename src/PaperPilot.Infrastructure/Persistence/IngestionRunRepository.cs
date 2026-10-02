using Microsoft.EntityFrameworkCore;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Persistence;

/// <summary>Reads and writes <see cref="IngestionRun"/> rows.</summary>
public sealed class IngestionRunRepository(PaperPilotDbContext db)
{
    /// <summary>Inserts a new run, or saves the changes to a run added before.</summary>
    public async Task SaveAsync(IngestionRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (db.Entry(run).State == EntityState.Detached)
        {
            db.IngestionRuns.Add(run);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The latest target date any succeeded run covered, or null before the first success.</summary>
    public Task<DateOnly?> GetLastSucceededTargetAsync(CancellationToken cancellationToken = default) =>
        db.IngestionRuns.Where(r => r.Status == IngestionRunStatus.Succeeded)
            .MaxAsync(r => (DateOnly?)r.TargetTo, cancellationToken);

    /// <summary>The newest runs first.</summary>
    public async Task<IReadOnlyList<IngestionRun>> ListAsync(int limit = 20, CancellationToken cancellationToken = default) =>
        await db.IngestionRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(limit).ToListAsync(cancellationToken);
}

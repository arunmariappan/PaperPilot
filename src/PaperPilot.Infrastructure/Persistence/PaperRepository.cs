using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PaperPilot.Core.Domain;

namespace PaperPilot.Infrastructure.Persistence;

/// <summary>Reads and writes <see cref="Paper"/> rows. Reads are not tracked.</summary>
public sealed class PaperRepository(PaperPilotDbContext db, TimeProvider timeProvider)
{
    /// <summary>Stored in <c>parser_metadata.note</c> when a paper has metadata only.</summary>
    public const string ParseFailedNote = "PDF processing not available or failed";

    public Task<Paper?> GetByArxivIdAsync(string arxivId, CancellationToken cancellationToken = default) =>
        db.Papers.AsNoTracking().SingleOrDefaultAsync(p => p.ArxivId == arxivId, cancellationToken);

    public Task<Paper?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.Papers.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>The given papers, in no particular order. Unknown ids are ignored.</summary>
    public async Task<IReadOnlyList<Paper>> GetByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default) =>
        await db.Papers.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);

    /// <summary>Newest publication first.</summary>
    public async Task<IReadOnlyList<Paper>> GetAllAsync(
        int limit = 100, int offset = 0, CancellationToken cancellationToken = default) =>
        await db.Papers.AsNoTracking()
            .OrderByDescending(p => p.PublishedDate)
            .Skip(offset).Take(limit)
            .ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        db.Papers.CountAsync(cancellationToken);

    /// <summary>Papers whose PDF was parsed, most recently parsed first.</summary>
    public async Task<IReadOnlyList<Paper>> GetProcessedAsync(
        int limit = 100, int offset = 0, CancellationToken cancellationToken = default) =>
        await db.Papers.AsNoTracking()
            .Where(p => p.PdfProcessed)
            .OrderByDescending(p => p.PdfProcessingDate)
            .Skip(offset).Take(limit)
            .ToListAsync(cancellationToken);

    /// <summary>Papers stored with metadata only, newest publication first.</summary>
    public async Task<IReadOnlyList<Paper>> GetUnprocessedAsync(
        int limit = 100, int offset = 0, CancellationToken cancellationToken = default) =>
        await db.Papers.AsNoTracking()
            .Where(p => !p.PdfProcessed)
            .OrderByDescending(p => p.PublishedDate)
            .Skip(offset).Take(limit)
            .ToListAsync(cancellationToken);

    /// <summary>Papers that have raw text, most recently parsed first.</summary>
    public async Task<IReadOnlyList<Paper>> GetWithRawTextAsync(
        int limit = 100, int offset = 0, CancellationToken cancellationToken = default) =>
        await db.Papers.AsNoTracking()
            .Where(p => p.RawText != null)
            .OrderByDescending(p => p.PdfProcessingDate)
            .Skip(offset).Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<PaperProcessingStats> GetProcessingStatsAsync(CancellationToken cancellationToken = default)
    {
        var total = await db.Papers.CountAsync(cancellationToken);
        var processed = await db.Papers.CountAsync(p => p.PdfProcessed, cancellationToken);
        var withText = await db.Papers.CountAsync(p => p.RawText != null, cancellationToken);

        return new PaperProcessingStats(
            total,
            processed,
            withText,
            ProcessingRate: total > 0 ? processed * 100.0 / total : 0,
            TextExtractionRate: processed > 0 ? withText * 100.0 / processed : 0);
    }

    /// <summary>
    /// Inserts or updates a paper by arXiv id. Metadata is always updated. Parsed content is written only when
    /// <see cref="PaperUpsert.Content"/> is set, so a failed re-parse never downgrades a paper parsed before (B23).
    /// </summary>
    public async Task<Paper> UpsertAsync(PaperUpsert upsert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upsert);
        var metadata = upsert.Metadata;

        var paper = await db.Papers.SingleOrDefaultAsync(p => p.ArxivId == metadata.ArxivId, cancellationToken);
        if (paper is null)
        {
            paper = new Paper
            {
                ArxivId = metadata.ArxivId,
                Title = metadata.Title,
                Abstract = metadata.Abstract,
                PdfUrl = metadata.PdfUrl,
            };
            db.Papers.Add(paper);
        }

        paper.Title = metadata.Title;
        paper.Authors = [.. metadata.Authors];
        paper.Abstract = metadata.Abstract;
        paper.Categories = [.. metadata.Categories];
        paper.PublishedDate = metadata.PublishedDate.ToUniversalTime();
        paper.PdfUrl = metadata.PdfUrl;

        if (upsert.Content is { } content)
        {
            paper.RawText = content.RawText;
            paper.Sections = [.. content.Sections];
            paper.References = [];
            paper.ParserUsed = content.ParserUsed;
            paper.ParserMetadata = JsonSerializer.SerializeToDocument(content.Metadata);
            paper.PdfProcessed = true;
            paper.PdfProcessingDate = timeProvider.GetUtcNow();
        }
        else if (!paper.PdfProcessed)
        {
            paper.ParserMetadata = JsonSerializer.SerializeToDocument(
                new Dictionary<string, string> { ["note"] = ParseFailedNote });
        }

        await db.SaveChangesAsync(cancellationToken);
        return paper;
    }
}

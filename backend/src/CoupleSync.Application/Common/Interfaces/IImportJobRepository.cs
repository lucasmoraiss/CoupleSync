using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IImportJobRepository
{
    Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task AddAsync(ImportJob job, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>Discards the in-memory copy of <paramref name="job"/> and reads it again, e.g. after a concurrency conflict.</summary>
    Task ReloadAsync(ImportJob job, CancellationToken ct);
    Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct);

    /// <summary>Jobs still in Processing whose last update is at or before <paramref name="cutoffUtc"/> (all couples).</summary>
    /// <summary>The couple's jobs that are Ready (newest first): reviewed or not, possibly with lines still pending.</summary>
    Task<IReadOnlyList<ImportJob>> GetReadyByCoupleAsync(Guid coupleId, int limit, CancellationToken ct);

    Task<IReadOnlyList<ImportJob>> GetStuckProcessingAsync(DateTime cutoffUtc, int limit, CancellationToken ct);
}

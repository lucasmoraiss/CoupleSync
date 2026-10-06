using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IImportJobRepository
{
    Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task AddAsync(ImportJob job, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct);

    /// <summary>Jobs still in Processing whose last update is at or before <paramref name="cutoffUtc"/> (all couples).</summary>
    Task<IReadOnlyList<ImportJob>> GetStuckProcessingAsync(DateTime cutoffUtc, int limit, CancellationToken ct);
}

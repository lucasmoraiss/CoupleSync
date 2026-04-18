using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IImportJobRepository
{
    Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task AddAsync(ImportJob job, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct);
}

using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IIncomeSourceRepository
{
    Task<IReadOnlyList<IncomeSource>> GetByMonthAsync(Guid coupleId, string month, CancellationToken ct);
    Task<IncomeSource?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task<int> CountByUserAndMonthAsync(Guid userId, Guid coupleId, string month, CancellationToken ct);
    Task AddAsync(IncomeSource source, CancellationToken ct);
    Task DeleteAsync(IncomeSource source, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

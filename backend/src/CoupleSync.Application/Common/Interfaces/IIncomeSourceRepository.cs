using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IIncomeSourceRepository
{
    /// <summary>
    /// Sources that can count in some month of [fromMonth, toMonth] ("yyyy-MM"): those registered in the range
    /// plus the recurring ones registered in or before it. Resolve per month with <c>IncomeSchedule</c>.
    /// </summary>
    Task<IReadOnlyList<IncomeSource>> GetCandidatesForMonthsAsync(Guid coupleId, string fromMonth, string toMonth, CancellationToken ct);
    Task<IncomeSource?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct);
    Task<int> CountByUserAndMonthAsync(Guid userId, Guid coupleId, string month, CancellationToken ct);
    Task AddAsync(IncomeSource source, CancellationToken ct);
    Task DeleteAsync(IncomeSource source, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

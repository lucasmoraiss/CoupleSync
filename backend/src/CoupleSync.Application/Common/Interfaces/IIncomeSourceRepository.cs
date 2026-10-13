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

    /// <summary>The names of the sources the person registered in the month ("yyyy-MM"), as stored.</summary>
    Task<IReadOnlyList<string>> GetNamesByUserAndMonthAsync(Guid userId, Guid coupleId, string month, CancellationToken ct);

    /// <summary>
    /// Of <paramref name="ids"/>, the sources of the group created from an entry of the bank (Open Finance): the ones
    /// a line of the mirror points to.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetIdsCreatedFromBankAsync(Guid coupleId, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// The line of the bank the source was created from (when there is one) goes back to the review as discarded,
    /// without the link, in the next save: it can be restored and registered again.
    /// </summary>
    Task ReleaseBankLinesAsync(Guid sourceId, Guid coupleId, DateTime nowUtc, CancellationToken ct);
    Task AddAsync(IncomeSource source, CancellationToken ct);
    Task DeleteAsync(IncomeSource source, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

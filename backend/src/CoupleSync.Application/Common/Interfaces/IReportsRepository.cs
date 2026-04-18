using CoupleSync.Application.Reports;

namespace CoupleSync.Application.Common.Interfaces;

public interface IReportsRepository
{
    Task<IReadOnlyList<CategorySpendingRow>> GetSpendingByCategoryAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct);

    Task<IReadOnlyList<MonthlySpendingRow>> GetMonthlySpendingAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct);
}

using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IBudgetRepository
{
    Task<BudgetPlan?> GetByMonthAsync(Guid coupleId, string month, CancellationToken ct);
    Task<BudgetPlan?> GetByIdAsync(Guid planId, Guid coupleId, CancellationToken ct);
    Task AddAsync(BudgetPlan plan, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);

    /// <summary>
    /// Transactionally removes all existing allocations for the plan and inserts the new list.
    /// Returns the plan with the refreshed Allocations collection.
    /// </summary>
    Task<BudgetPlan> ReplaceAllocationsAsync(
        BudgetPlan plan,
        IReadOnlyList<(string Category, decimal AllocatedAmount, string Currency)> newAllocations,
        DateTime nowUtc,
        CancellationToken ct);
}

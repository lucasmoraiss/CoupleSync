using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeBudgetRepository : IBudgetRepository
{
    public List<BudgetPlan> Plans { get; } = new();

    public Task<BudgetPlan?> GetByMonthAsync(Guid coupleId, string month, CancellationToken ct)
    {
        var plan = Plans.FirstOrDefault(p => p.CoupleId == coupleId && p.Month == month);
        return Task.FromResult(plan);
    }

    public Task<BudgetPlan?> GetByIdAsync(Guid planId, Guid coupleId, CancellationToken ct)
    {
        var plan = Plans.FirstOrDefault(p => p.Id == planId && p.CoupleId == coupleId);
        return Task.FromResult(plan);
    }

    public Task AddAsync(BudgetPlan plan, CancellationToken ct)
    {
        Plans.Add(plan);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => Task.CompletedTask;

    public Task<BudgetPlan> ReplaceAllocationsAsync(
        BudgetPlan plan,
        IReadOnlyList<(string Category, decimal AllocatedAmount, string Currency)> newAllocations,
        DateTime nowUtc,
        CancellationToken ct)
    {
        plan.Allocations.Clear();
        foreach (var (category, amount, currency) in newAllocations)
        {
            plan.Allocations.Add(BudgetAllocation.Create(plan.Id, category, amount, currency, nowUtc));
        }
        return Task.FromResult(plan);
    }
}

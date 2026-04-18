using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class BudgetRepository : IBudgetRepository
{
    private readonly AppDbContext _dbContext;

    public BudgetRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<BudgetPlan?> GetByMonthAsync(Guid coupleId, string month, CancellationToken ct)
        => _dbContext.BudgetPlans
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.CoupleId == coupleId && p.Month == month, ct);

    public Task<BudgetPlan?> GetByIdAsync(Guid planId, Guid coupleId, CancellationToken ct)
        => _dbContext.BudgetPlans
            .Include(p => p.Allocations)
            .FirstOrDefaultAsync(p => p.Id == planId && p.CoupleId == coupleId, ct);

    public Task AddAsync(BudgetPlan plan, CancellationToken ct)
        => _dbContext.BudgetPlans.AddAsync(plan, ct).AsTask();

    public Task SaveChangesAsync(CancellationToken ct)
        => _dbContext.SaveChangesAsync(ct);

    public async Task<BudgetPlan> ReplaceAllocationsAsync(
        BudgetPlan plan,
        IReadOnlyList<(string Category, decimal AllocatedAmount, string Currency)> newAllocations,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var tx = await _dbContext.Database.BeginTransactionAsync(ct);

        _dbContext.RemoveRange(plan.Allocations.ToList());

        foreach (var (category, amount, currency) in newAllocations)
        {
            var allocation = BudgetAllocation.Create(plan.Id, category, amount, currency, nowUtc);
            await _dbContext.Set<BudgetAllocation>().AddAsync(allocation, ct);
        }

        await _dbContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Reload the updated allocations into the tracked plan
        await _dbContext.Entry(plan).Collection(p => p.Allocations).LoadAsync(ct);

        return plan;
    }
}

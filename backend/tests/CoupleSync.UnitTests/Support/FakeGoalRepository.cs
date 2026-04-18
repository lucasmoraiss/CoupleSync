using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeGoalRepository : IGoalRepository
{
    public List<Goal> Goals { get; } = new();

    public Task<(int TotalCount, IReadOnlyList<Goal> Items)> GetPagedAsync(
        Guid coupleId,
        bool includeArchived,
        CancellationToken ct)
    {
        var query = Goals.Where(g => g.CoupleId == coupleId);

        if (!includeArchived)
            query = query.Where(g => g.Status == GoalStatus.Active);

        var items = query.ToList();
        return Task.FromResult<(int, IReadOnlyList<Goal>)>((items.Count, items));
    }

    public Task<Goal?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        return Task.FromResult(Goals.FirstOrDefault(g => g.Id == id && g.CoupleId == coupleId));
    }

    public Task AddAsync(Goal goal, CancellationToken ct)
    {
        Goals.Add(goal);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

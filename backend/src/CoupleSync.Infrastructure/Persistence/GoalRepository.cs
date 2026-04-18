using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class GoalRepository : IGoalRepository
{
    private readonly AppDbContext _dbContext;

    public GoalRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<(int TotalCount, IReadOnlyList<Goal> Items)> GetPagedAsync(
        Guid coupleId,
        bool includeArchived,
        CancellationToken ct)
    {
        var query = _dbContext.Goals
            .Where(g => g.CoupleId == coupleId)
            .AsQueryable();

        if (!includeArchived)
            query = query.Where(g => g.Status == GoalStatus.Active);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(g => g.CreatedAtUtc)
            .ToListAsync(ct);

        return (totalCount, items);
    }

    public async Task<Goal?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.Goals
            .FirstOrDefaultAsync(g => g.Id == id && g.CoupleId == coupleId, ct);
    }

    public async Task AddAsync(Goal goal, CancellationToken ct)
    {
        await _dbContext.Goals.AddAsync(goal, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}

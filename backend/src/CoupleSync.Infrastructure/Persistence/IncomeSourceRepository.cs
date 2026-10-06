using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class IncomeSourceRepository : IIncomeSourceRepository
{
    private readonly AppDbContext _dbContext;

    public IncomeSourceRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<IncomeSource>> GetCandidatesForMonthsAsync(
        Guid coupleId, string fromMonth, string toMonth, CancellationToken ct)
    {
        // A group has a handful of sources per month, so the month comparison is done in memory
        // ("yyyy-MM" text; string ordering in SQL depends on the database collation).
        var sources = await _dbContext.IncomeSources
            .Where(s => s.CoupleId == coupleId)
            .OrderBy(s => s.CreatedAtUtc)
            .ToListAsync(ct);

        return sources
            .Where(s => string.CompareOrdinal(s.Month, toMonth) <= 0
                && (s.IsRecurring || string.CompareOrdinal(s.Month, fromMonth) >= 0))
            .ToList();
    }

    public Task<IncomeSource?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
        => _dbContext.IncomeSources
            .FirstOrDefaultAsync(s => s.Id == id && s.CoupleId == coupleId, ct);

    public async Task<int> CountByUserAndMonthAsync(Guid userId, Guid coupleId, string month, CancellationToken ct)
        => await _dbContext.IncomeSources
            .CountAsync(s => s.UserId == userId && s.CoupleId == coupleId && s.Month == month, ct);

    public Task AddAsync(IncomeSource source, CancellationToken ct)
        => _dbContext.IncomeSources.AddAsync(source, ct).AsTask();

    public Task DeleteAsync(IncomeSource source, CancellationToken ct)
    {
        _dbContext.IncomeSources.Remove(source);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
        => _dbContext.SaveChangesAsync(ct);
}

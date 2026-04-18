using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class TransactionRepository : ITransactionRepository
{
    private readonly AppDbContext _dbContext;

    public TransactionRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AnyAsync(t => t.Fingerprint == fingerprint && t.CoupleId == coupleId, ct);
    }

    public async Task AddTransactionAsync(Transaction transaction, CancellationToken ct)
    {
        await _dbContext.Transactions.AddAsync(transaction, ct);
    }

    public async Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId,
        int page,
        int pageSize,
        string? category,
        DateTime? startDate,
        DateTime? endDate,
        CancellationToken ct)
    {
        var query = _dbContext.Transactions
            .Where(t => t.CoupleId == coupleId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(t => t.Category == category);

        if (startDate.HasValue)
            query = query.Where(t => t.EventTimestampUtc >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(t => t.EventTimestampUtc <= endDate.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(t => t.EventTimestampUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (totalCount, items);
    }

    public async Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.Transactions.FirstOrDefaultAsync(t => t.Id == id && t.CoupleId == coupleId, ct);
    }

    public async Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.Transactions
            .Where(t => t.GoalId == goalId && t.CoupleId == coupleId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct)
    {
        return await _dbContext.Transactions
            .Where(t => t.CoupleId == coupleId && t.EventTimestampUtc >= since)
            .ToListAsync(ct);
    }

    public Task UpdateAsync(Transaction transaction, CancellationToken ct = default)
    {
        _dbContext.Transactions.Update(transaction);
        return Task.CompletedTask;
    }

    public async Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(
        Guid coupleId,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken ct)
    {
        // Materialise to memory before GroupBy: EF Core / SQLite cannot translate
        // Sum(decimal) to SQL, so we apply the aggregation in LINQ to Objects.
        var rows = await _dbContext.Transactions
            .Where(t => t.CoupleId == coupleId
                        && t.EventTimestampUtc >= startUtc
                        && t.EventTimestampUtc < endUtc)
            .Select(t => new { t.Category, t.Amount })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Category)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}

using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Reports;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class ReportsRepository : IReportsRepository
{
    private readonly AppDbContext _dbContext;

    public ReportsRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CategorySpendingRow>> GetSpendingByCategoryAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var baseQuery = _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                && t.EventTimestampUtc >= fromUtc
                && t.EventTimestampUtc <= toUtc);

        var isSqlite = _dbContext.Database.ProviderName
            ?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        if (isSqlite)
        {
            var local = await baseQuery.Select(t => new { t.Category, t.Amount }).ToListAsync(ct);
            return local
                .GroupBy(r => r.Category)
                .Select(g => new CategorySpendingRow(g.Key, g.Sum(r => r.Amount)))
                .ToList();
        }

        return await baseQuery
            .GroupBy(t => t.Category)
            .Select(g => new CategorySpendingRow(g.Key, g.Sum(t => t.Amount)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MonthlySpendingRow>> GetMonthlySpendingAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var baseQuery = _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                && t.EventTimestampUtc >= fromUtc
                && t.EventTimestampUtc <= toUtc);

        var isSqlite = _dbContext.Database.ProviderName
            ?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        if (isSqlite)
        {
            var local = await baseQuery
                .Select(t => new { t.EventTimestampUtc, t.Amount })
                .ToListAsync(ct);

            return local
                .GroupBy(r => (r.EventTimestampUtc.Year, r.EventTimestampUtc.Month))
                .Select(g => new MonthlySpendingRow(g.Key.Year, g.Key.Month, g.Sum(r => r.Amount)))
                .ToList();
        }

        return await baseQuery
            .GroupBy(t => new { t.EventTimestampUtc.Year, t.EventTimestampUtc.Month })
            .Select(g => new MonthlySpendingRow(g.Key.Year, g.Key.Month, g.Sum(t => t.Amount)))
            .ToListAsync(ct);
    }
}

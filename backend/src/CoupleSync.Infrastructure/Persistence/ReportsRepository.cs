using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Reports;
using CoupleSync.Domain.ValueObjects;
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
                && t.Currency == CurrencyRules.Brl
                && t.EventTimestampUtc >= fromUtc
                && t.EventTimestampUtc <= toUtc);

        var isSqlite = _dbContext.Database.ProviderName
            ?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        IEnumerable<(string Category, decimal Total)> perStoredCategory;
        if (isSqlite)
        {
            var local = await baseQuery.Select(t => new { t.Category, t.Amount }).ToListAsync(ct);
            perStoredCategory = local
                .GroupBy(r => r.Category)
                .Select(g => (g.Key, g.Sum(r => r.Amount)));
        }
        else
        {
            var grouped = await baseQuery
                .GroupBy(t => t.Category)
                .Select(g => new { Category = g.Key, Total = g.Sum(t => t.Amount) })
                .ToListAsync(ct);
            perStoredCategory = grouped.Select(g => (g.Category, g.Total));
        }

        // "Alimentação", "ALIMENTACAO" and "alimentacao" are one category.
        return perStoredCategory
            .GroupBy(r => TransactionCategories.NormalizeOrOther(r.Category))
            .Select(g => new CategorySpendingRow(g.Key, g.Sum(r => r.Total)))
            .ToList();
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
                && t.Currency == CurrencyRules.Brl
                && t.EventTimestampUtc >= fromUtc
                && t.EventTimestampUtc <= toUtc);

        // The month is a Brasília month, which no provider can derive from the UTC column in SQL.
        var local = await baseQuery
            .Select(t => new { t.EventTimestampUtc, t.Amount })
            .ToListAsync(ct);

        return local
            .Select(r => (Local: BrazilTime.ToLocal(r.EventTimestampUtc), r.Amount))
            .GroupBy(r => (r.Local.Year, r.Local.Month))
            .Select(g => new MonthlySpendingRow(g.Key.Year, g.Key.Month, g.Sum(r => r.Amount)))
            .ToList();
    }
}

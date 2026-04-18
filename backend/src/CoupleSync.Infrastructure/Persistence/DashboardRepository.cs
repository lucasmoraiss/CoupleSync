using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Dashboard;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class DashboardRepository : IDashboardRepository
{
    private readonly AppDbContext _dbContext;

    public DashboardRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DashboardAggregates> GetAggregatesAsync(
        Guid coupleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct)
    {
        var baseQuery = _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                && t.EventTimestampUtc >= startDate
                && t.EventTimestampUtc <= endDate);

        var transactionCount = await baseQuery.CountAsync(ct);

        if (transactionCount == 0)
        {
            return new DashboardAggregates(
                0, 0m,
                new Dictionary<string, decimal>(),
                new List<PartnerBreakdownItem>());
        }

        // BOTH branches below must stay in sync with each other and with DashboardAggregates fields.
        // SQLite (integration tests) cannot aggregate decimal at SQL level.
        // Fall back to a projected in-memory aggregation for SQLite; use SQL GROUP BY for PostgreSQL.
        // NOTE: constraint explicitly permits this fallback — see T-011 constraints.
        var isSqlite = _dbContext.Database.ProviderName
            ?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

        if (isSqlite)
        {
            var rows = await baseQuery
                .Select(t => new { t.Category, t.UserId, t.Amount })
                .ToListAsync(ct);

            var expensesByCategoryLocal = rows
                .GroupBy(r => r.Category)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

            var partnerBreakdownLocal = rows
                .GroupBy(r => r.UserId)
                .Select(g => new PartnerBreakdownItem(g.Key, g.Sum(r => r.Amount)))
                .ToList();

            return new DashboardAggregates(
                transactionCount,
                rows.Sum(r => r.Amount),
                expensesByCategoryLocal,
                partnerBreakdownLocal);
        }

        // SQL GROUP BY path — PostgreSQL only.
        var categoryRows = await baseQuery
            .GroupBy(t => t.Category)
            .Select(g => new { Category = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync(ct);

        var totalExpenses = categoryRows.Sum(x => x.Total);

        var partnerRows = await baseQuery
            .GroupBy(t => t.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(t => t.Amount) })
            .ToListAsync(ct);

        return new DashboardAggregates(
            transactionCount,
            totalExpenses,
            categoryRows.ToDictionary(x => x.Category, x => x.Total),
            partnerRows.Select(x => new PartnerBreakdownItem(x.UserId, x.Total)).ToList());
    }
}

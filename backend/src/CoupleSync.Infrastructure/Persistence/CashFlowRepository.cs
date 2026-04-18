using CoupleSync.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class CashFlowRepository : ICashFlowRepository
{
    private readonly AppDbContext _dbContext;

    public CashFlowRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CashFlowData> GetHistoricalDataAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        // Global ICoupleScoped query filter fires first; explicit couple check is defence-in-depth.
        // Outgoing transactions only — credits and zero-amount entries are excluded from projection.
        var transactions = await _dbContext.Transactions
            .AsNoTracking()
            .Where(t => t.CoupleId == coupleId
                     && t.Amount > 0
                     && t.EventTimestampUtc >= fromUtc
                     && t.EventTimestampUtc <= toUtc)
            .ToListAsync(ct);

        var totalSpend = transactions.Sum(t => t.Amount);

        var categoryBreakdown = (IReadOnlyDictionary<string, decimal>)transactions
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        return new CashFlowData(
            transactions.Count,
            totalSpend,
            categoryBreakdown);
    }
}

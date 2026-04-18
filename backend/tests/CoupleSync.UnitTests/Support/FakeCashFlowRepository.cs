using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeCashFlowRepository : ICashFlowRepository
{
    private readonly List<(Guid CoupleId, decimal Amount, string Category, DateTime EventTimestampUtc)> _transactions = new();

    public void AddTransaction(Guid coupleId, decimal amount, string category, DateTime eventTimestampUtc)
    {
        _transactions.Add((coupleId, amount, category, eventTimestampUtc));
    }

    public Task<CashFlowData> GetHistoricalDataAsync(
        Guid coupleId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct)
    {
        var filtered = _transactions
            .Where(t => t.CoupleId == coupleId
                     && t.Amount > 0
                     && t.EventTimestampUtc >= fromUtc
                     && t.EventTimestampUtc <= toUtc)
            .ToList();

        var totalSpend = filtered.Sum(t => t.Amount);

        var categoryBreakdown = filtered
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount))
            as IReadOnlyDictionary<string, decimal>;

        return Task.FromResult(new CashFlowData(
            filtered.Count,
            totalSpend,
            categoryBreakdown ?? new Dictionary<string, decimal>()));
    }
}

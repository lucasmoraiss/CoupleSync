using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Dashboard;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeDashboardRepository : IDashboardRepository
{
    public List<Transaction> Transactions { get; } = new();

    public Task<DashboardAggregates> GetAggregatesAsync(
        Guid coupleId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct)
    {
        var filtered = Transactions
            .Where(t => t.CoupleId == coupleId
                && t.EventTimestampUtc >= startDate
                && t.EventTimestampUtc <= endDate)
            .ToList();

        var totalExpenses = filtered.Sum(t => t.Amount);
        var transactionCount = filtered.Count;

        var expensesByCategory = filtered
            .GroupBy(t => t.Category)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        var partnerBreakdown = filtered
            .GroupBy(t => t.UserId)
            .Select(g => new PartnerBreakdownItem(g.Key, g.Sum(t => t.Amount)))
            .ToList();

        return Task.FromResult(new DashboardAggregates(
            transactionCount,
            totalExpenses,
            expensesByCategory,
            partnerBreakdown));
    }
}

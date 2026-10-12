using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Dashboard;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeDashboardRepository : IDashboardRepository
{
    public List<Transaction> Transactions { get; } = new();

    /// <summary>The bills of the cards of the group; null: nobody of the group is connected to a bank.</summary>
    public List<UpcomingBill>? Bills { get; set; }

    /// <summary>The (from, to) of every question about bills.</summary>
    public List<(DateOnly From, DateOnly To)> BillWindowsAsked { get; } = new();

    public Task<IReadOnlyList<UpcomingBill>?> GetUpcomingBillsAsync(Guid coupleId, DateOnly fromDue, DateOnly toDue, CancellationToken ct)
    {
        BillWindowsAsked.Add((fromDue, toDue));
        IReadOnlyList<UpcomingBill>? found = Bills?.Where(b => b.DueDate >= fromDue && b.DueDate <= toDue).ToList();
        return Task.FromResult(found);
    }

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

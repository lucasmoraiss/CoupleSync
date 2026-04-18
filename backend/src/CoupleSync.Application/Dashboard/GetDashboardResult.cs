namespace CoupleSync.Application.Dashboard;

public sealed record GetDashboardResult(
    decimal TotalExpenses,
    IReadOnlyDictionary<string, decimal> ExpensesByCategory,
    IReadOnlyList<PartnerBreakdownItem> PartnerBreakdown,
    int TransactionCount,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime GeneratedAtUtc);

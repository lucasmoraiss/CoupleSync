namespace CoupleSync.Application.Dashboard;

public sealed record DashboardAggregates(
    int TransactionCount,
    decimal TotalExpenses,
    IReadOnlyDictionary<string, decimal> ExpensesByCategory,
    IReadOnlyList<PartnerBreakdownItem> PartnerBreakdown);

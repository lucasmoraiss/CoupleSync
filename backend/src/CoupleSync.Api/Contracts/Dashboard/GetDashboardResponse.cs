namespace CoupleSync.Api.Contracts.Dashboard;

public sealed record PartnerBreakdownResponse(Guid UserId, decimal TotalAmount);

public sealed record GetDashboardResponse(
    decimal TotalExpenses,
    Dictionary<string, decimal> ExpensesByCategory,
    List<PartnerBreakdownResponse> PartnerBreakdown,
    int TransactionCount,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime GeneratedAtUtc);

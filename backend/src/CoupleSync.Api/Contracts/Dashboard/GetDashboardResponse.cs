namespace CoupleSync.Api.Contracts.Dashboard;

public sealed record PartnerBreakdownResponse(Guid UserId, decimal TotalAmount);

public sealed record GetDashboardResponse(
    decimal TotalExpenses,
    Dictionary<string, decimal> ExpensesByCategory,
    List<PartnerBreakdownResponse> PartnerBreakdown,
    int TransactionCount,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime GeneratedAtUtc,
    // New, additive (issue #27): null when nobody of the group is connected to a bank. An app that does not know
    // the field ignores it.
    UpcomingBillsResponse? UpcomingBills = null);

/// <summary>
/// The bills of the credit cards of the group due from <c>FromDay</c> (today in Brazil) to <c>ToDay</c> (thirty days
/// later), earliest first. <c>TotalBrl</c> adds only what is owed in reais.
/// </summary>
public sealed record UpcomingBillsResponse(decimal TotalBrl, DateOnly FromDay, DateOnly ToDay, List<UpcomingBillResponse> Bills);

/// <summary><c>TotalAmount</c> is in <c>Currency</c>: never converted.</summary>
public sealed record UpcomingBillResponse(
    Guid Id,
    Guid AccountId,
    string CardName,
    string BankName,
    Guid UserId,
    string? UserName,
    DateOnly DueDate,
    decimal TotalAmount,
    string Currency,
    bool IsClosed);

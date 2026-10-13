namespace CoupleSync.Application.Dashboard;

public sealed record GetDashboardResult(
    decimal TotalExpenses,
    IReadOnlyDictionary<string, decimal> ExpensesByCategory,
    IReadOnlyList<PartnerBreakdownItem> PartnerBreakdown,
    int TransactionCount,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    DateTime GeneratedAtUtc,
    // Null when nobody of the group is connected to a bank (Open Finance): the dashboard shows nothing about bills.
    UpcomingBills? UpcomingBills = null);

/// <summary>
/// The bills of the credit cards of the group due from <paramref name="FromDay"/> (today in Brazil) to
/// <paramref name="ToDay"/>, earliest first. <paramref name="TotalBrl"/> adds only what is owed in reais: a bill in
/// another currency, of zero or with a credit is listed and never added.
/// </summary>
public sealed record UpcomingBills(decimal TotalBrl, DateOnly FromDay, DateOnly ToDay, IReadOnlyList<UpcomingBill> Bills);

/// <summary>One bill due soon. <paramref name="TotalAmount"/> is in <paramref name="Currency"/>. Prints no name.</summary>
public sealed record UpcomingBill(
    Guid Id,
    Guid AccountId,
    string CardName,
    string BankName,
    Guid UserId,
    string? UserName,
    DateOnly DueDate,
    decimal TotalAmount,
    string Currency,
    bool IsClosed)
{
    public override string ToString() => nameof(UpcomingBill);
}

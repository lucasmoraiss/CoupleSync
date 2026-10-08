namespace CoupleSync.Api.Contracts.Ai;

/// <param name="Override">"NotRecurring", "Cancelled", "Subscription", "FixedBill", or null to take the correction back.</param>
public sealed record RecurringOverrideRequest(string? Override);

public sealed record RecurringPersonResponse(Guid UserId, string Name);

/// <param name="EndMonth">"yyyy-MM" of the last instalment.</param>
public sealed record RecurringInstallmentResponse(int Number, int Total, decimal RemainingAmount, string EndMonth);

/// <param name="Kind">"Subscription", "FixedBill", "Installment" or "Habit" (what the person said it is, when they did).</param>
/// <param name="Cadence">"Weekly", "Monthly", "Yearly", or "Irregular" for a small frequent spend.</param>
/// <param name="Amount">The typical amount of today (the median of the last 3 charges when <paramref name="VariableAmount"/>).</param>
/// <param name="FirstSeen">Brasília dates, "yyyy-MM-dd".</param>
/// <param name="Status">"Active", "SuspectedDormant" or "Stopped".</param>
/// <param name="Flags">"Forgotten", "PriceIncrease", "New", "ChargedAfterCancel".</param>
/// <param name="Confidence">"High", "Medium" or "Low" (a probable instalment).</param>
public sealed record RecurringItemResponse(
    Guid Id,
    string Name,
    string Kind,
    bool VariableAmount,
    string Cadence,
    decimal Amount,
    decimal LastAmount,
    decimal? PreviousAmount,
    decimal AnnualCost,
    int Occurrences,
    int MissedCount,
    string FirstSeen,
    string LastSeen,
    string? NextExpected,
    string Status,
    IReadOnlyList<string> Flags,
    string Confidence,
    string Category,
    RecurringPersonResponse? Person,
    RecurringInstallmentResponse? Installment,
    string? Override);

/// <param name="Month">"yyyy-MM".</param>
public sealed record RecurringMonthCommitmentResponse(string Month, decimal Amount);

/// <param name="MonthlyTotal">Subscriptions, fixed bills and confirmed instalments of the active list, per month.</param>
/// <param name="AnnualTotal">The same, in 12 months (instalments: only the parts still to be paid).</param>
/// <param name="Hidden">What the person said is not recurring or cancelled, and what stopped being charged.</param>
/// <param name="InstallmentsByMonth">What the confirmed instalments still commit in each of the next 12 months.</param>
public sealed record RecurringListResponse(
    decimal MonthlyTotal,
    decimal AnnualTotal,
    DateTime DetectedAtUtc,
    IReadOnlyList<RecurringItemResponse> Subscriptions,
    IReadOnlyList<RecurringItemResponse> FixedBills,
    IReadOnlyList<RecurringItemResponse> Installments,
    IReadOnlyList<RecurringItemResponse> Habits,
    IReadOnlyList<RecurringItemResponse> Hidden,
    IReadOnlyList<RecurringMonthCommitmentResponse> InstallmentsByMonth);

/// <param name="Date">The instant of the charge (UTC).</param>
/// <param name="Merchant">The establishment as the transaction list shows it.</param>
public sealed record RecurringChargeResponse(Guid TransactionId, DateTime Date, decimal Amount, string Merchant);

public sealed record RecurringChargesResponse(IReadOnlyList<RecurringChargeResponse> Transactions);

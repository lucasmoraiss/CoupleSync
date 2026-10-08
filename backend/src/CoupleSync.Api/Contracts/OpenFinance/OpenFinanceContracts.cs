namespace CoupleSync.Api.Contracts.OpenFinance;

/// <summary>
/// The requests that carry credentials print nothing of them: MVC writes the arguments of an action to the log
/// (Trace level) through <c>ToString()</c>, and a record would print every property.
/// </summary>
public sealed record TestCredentialsRequest(string ClientId, string ClientSecret)
{
    public override string ToString() => nameof(TestCredentialsRequest);
}

public sealed record TestCredentialsResponse(bool Valid);

/// <summary><c>HistoryMonths</c> is optional (3 when absent); allowed: 3, 6 or 12.</summary>
public sealed record CreateBankConnectionRequest(string Label, string ClientId, string ClientSecret, int? HistoryMonths)
{
    public override string ToString() => nameof(CreateBankConnectionRequest);
}

public sealed record AddBankItemRequest(string ItemId);

/// <summary>Nullable so that a body without the field is a validation error instead of "false".</summary>
public sealed record UpdateBankAccountRequest(bool? SyncEnabled);

/// <summary>No response of these routes carries the client secret nor the whole client id.</summary>
public sealed record OpenFinanceStatusResponse(bool Available, IReadOnlyList<BankConnectionResponse> Connections);

public sealed record BankConnectionResponse(
    Guid Id,
    string Label,
    Guid UserId,
    string UserName,
    bool IsMine,
    string Status,
    string? ClientIdHint,
    int HistoryMonths,
    DateTime? LastSyncAtUtc,
    string? LastErrorCode,
    string? LastErrorMessage,
    DateTime CreatedAtUtc,
    IReadOnlyList<BankItemResponse> Items);

public sealed record BankItemResponse(
    Guid Id,
    string ConnectorName,
    string Status,
    string? ExecutionStatus,
    DateTime? LastUpdatedAtUtc,
    string? LastErrorMessage,
    IReadOnlyList<BankAccountResponse> Accounts);

public sealed record BankAccountResponse(
    Guid Id,
    string Type,
    string? Subtype,
    string Name,
    string? MarketingName,
    string? NumberMasked,
    string Currency,
    decimal Balance,
    DateTime BalanceAtUtc,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateOnly? BalanceCloseDate,
    DateOnly? BalanceDueDate,
    decimal? MinimumPayment,
    string? Brand,
    bool SyncEnabled);

/// <summary>A synchronisation of a connection with Pluggy: waiting, running, done or failed (with the reason, in Portuguese).</summary>
public sealed record SyncRunResponse(
    Guid Id,
    Guid ConnectionId,
    string Status,
    string TriggeredBy,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    int TransactionsNew,
    int TransactionsUpdated,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// A bank transaction waiting for the review (or discarded). <c>Amount</c> is the value of the expense, positive.
/// <c>BankStatus</c> is <c>Posted</c> or <c>Pending</c> (not settled at the bank yet: cannot be confirmed).
/// </summary>
public sealed record BankReviewLineResponse(
    Guid Id,
    DateOnly Day,
    string? Merchant,
    string? Description,
    decimal Amount,
    string Currency,
    string SuggestedCategory,
    string BankStatus,
    string BankName,
    string AccountName,
    int? InstallmentNumber,
    int? InstallmentTotal);

public sealed record BankReviewMonthResponse(string Month, int Pending);

/// <summary>
/// The review of a month (of Brazil). <c>PendingTotalBrl</c> adds only the expenses in reais.
/// <c>PendingAllMonths</c> counts, in every month, the expenses that can be confirmed (settled at the bank, with a
/// value, in reais). <c>PendingByMonth</c> counts everything that waits in each month, also what can only be discarded.
/// </summary>
public sealed record BankReviewResponse(
    string Month,
    IReadOnlyList<BankReviewLineResponse> Expenses,
    IReadOnlyList<BankReviewLineResponse> Discarded,
    decimal PendingTotalBrl,
    int PendingAllMonths,
    IReadOnlyList<BankReviewMonthResponse> PendingByMonth);

/// <summary>The value is never editable: only the category and the description.</summary>
public sealed record ConfirmExpenseRequest(Guid Id, string? Category, string? Description);

public sealed record ConfirmBankReviewRequest(IReadOnlyList<ConfirmExpenseRequest>? Expenses, IReadOnlyList<Guid>? Discard);

public sealed record BankReviewCreatedResponse(Guid Id, Guid TransactionId);

/// <summary>
/// <c>Skipped</c>: lines sent in <c>expenses</c> that cannot become an expense: no value (zero), or a currency that
/// is not BRL. They do not fail the request; they stay in the review, where they can be discarded.
/// <c>SkippedOtherCurrency</c>: the ones of <c>Skipped</c> that were skipped for the currency.
/// </summary>
public sealed record ConfirmBankReviewResponse(
    IReadOnlyList<BankReviewCreatedResponse> Created,
    IReadOnlyList<Guid> Discarded,
    int AlreadyConfirmed,
    IReadOnlyList<Guid> Skipped,
    IReadOnlyList<Guid> SkippedOtherCurrency);

public sealed record RestoreBankReviewResponse(IReadOnlyList<Guid> Restored);

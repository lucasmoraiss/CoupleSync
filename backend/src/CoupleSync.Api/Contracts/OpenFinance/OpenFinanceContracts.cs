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
    int? InstallmentTotal,
    string Type,
    string? AutoReason,
    BankReviewMatchResponse? Match);

/// <summary>
/// The transaction of the app a waiting line looks the same as (suspected duplicate). <c>Source</c> is how it entered
/// the app (<c>Manual</c>, <c>OcrImport</c> or <c>Notification</c>); <c>Day</c> is its day in Brazil.
/// </summary>
public sealed record BankReviewMatchResponse(
    Guid TransactionId,
    string Source,
    string? Description,
    string? Merchant,
    decimal Amount,
    string Currency,
    DateOnly Day);

public sealed record BankReviewMonthResponse(string Month, int Pending);

public sealed record BankReviewIgnoredMonthResponse(string Month, int Ignored);

/// <summary>
/// The review of a month (of Brazil). <c>PendingTotalBrl</c> adds only the expenses in reais.
/// <c>PendingAllMonths</c> counts, in every month, the expenses that can be confirmed (settled at the bank, with a
/// value, in reais). <c>PendingByMonth</c> counts everything that waits in each month, also what can only be discarded.
/// <c>IgnoredAllMonths</c> counts, in every month, the lines ignored automatically, and <c>IgnoredByMonth</c> says
/// in which months they are (the latest first): the way to them when nothing waits for the review.
/// New with the entries (issue #27), all additive: <c>Incomes</c> are the entries (money that came in, <c>Type</c>
/// <c>Credit</c>) of the month waiting; <c>PendingIncomesAllMonths</c> and <c>PendingIncomesByMonth</c> count them
/// the way <c>PendingAllMonths</c> and <c>PendingByMonth</c> count the expenses (which they never enter). <c>Discarded</c> lists the discarded
/// lines of both kinds, told apart by <c>Type</c>.
/// </summary>
public sealed record BankReviewResponse(
    string Month,
    IReadOnlyList<BankReviewLineResponse> Expenses,
    IReadOnlyList<BankReviewLineResponse> Discarded,
    decimal PendingTotalBrl,
    int PendingAllMonths,
    IReadOnlyList<BankReviewMonthResponse> PendingByMonth,
    IReadOnlyList<BankReviewLineResponse> Ignored,
    int SuspectedDuplicates,
    int IgnoredAllMonths,
    IReadOnlyList<BankReviewIgnoredMonthResponse> IgnoredByMonth,
    IReadOnlyList<BankReviewLineResponse> Incomes,
    int PendingIncomesAllMonths,
    IReadOnlyList<BankReviewMonthResponse> PendingIncomesByMonth);

/// <summary>The value is never editable: only the category and the description.</summary>
public sealed record ConfirmExpenseRequest(Guid Id, string? Category, string? Description);

/// <summary>"It is the same": the line that is the transaction the app already had.</summary>
public sealed record ReconcileBankLineRequest(Guid Id);

/// <summary>An entry to register as an income of its month. <c>Name</c> is optional: without it, the description of the line.</summary>
public sealed record ConfirmIncomeRequest(Guid Id, string? Name);

/// <summary>
/// <c>Reconcile</c>: lines that are the same as the transaction they were suspected of repeating (nothing is created).
/// <c>AsNew</c>: suspected lines that are another expense (confirmed like <c>Expenses</c>, with the suggested category).
/// <c>Incomes</c>: entries (money that came in) that become an income of the month of their day in Brazil: shared,
/// not recurring, of who connected the account. All are optional: a request without them means what it always meant.
/// </summary>
public sealed record ConfirmBankReviewRequest(
    IReadOnlyList<ConfirmExpenseRequest>? Expenses,
    IReadOnlyList<Guid>? Discard,
    IReadOnlyList<ReconcileBankLineRequest>? Reconcile = null,
    IReadOnlyList<Guid>? AsNew = null,
    IReadOnlyList<ConfirmIncomeRequest>? Incomes = null);

public sealed record BankReviewCreatedResponse(Guid Id, Guid TransactionId);

/// <summary>An entry that became an income of the month, and that income.</summary>
public sealed record BankReviewIncomeResponse(Guid Id, Guid IncomeSourceId);

public sealed record BankReviewReconciledResponse(Guid Id, Guid TransactionId);

/// <summary>
/// <c>Skipped</c>: lines sent in <c>expenses</c> that cannot become an expense: no value (zero), or a currency that
/// is not BRL. They do not fail the request; they stay in the review, where they can be discarded.
/// <c>SkippedOtherCurrency</c>: the ones of <c>Skipped</c> that were skipped for the currency.
/// <c>Incomes</c> (new, additive): the entries registered as an income by this request. An entry skipped (no value,
/// another currency) or registered before is in <c>Skipped</c> or counted in <c>AlreadyConfirmed</c>, as an expense is.
/// </summary>
public sealed record ConfirmBankReviewResponse(
    IReadOnlyList<BankReviewCreatedResponse> Created,
    IReadOnlyList<Guid> Discarded,
    int AlreadyConfirmed,
    IReadOnlyList<Guid> Skipped,
    IReadOnlyList<Guid> SkippedOtherCurrency,
    IReadOnlyList<BankReviewReconciledResponse> Reconciled,
    IReadOnlyList<BankReviewIncomeResponse> Incomes);

public sealed record RestoreBankReviewResponse(IReadOnlyList<Guid> Restored);

// ---------------------------------------------------------------- accounts and bills (GET accounts)

/// <summary>
/// The accounts and cards the group connected, by person; who asks comes first. Only connections still connected.
/// No response carries a credential, a whole account or card number, nor the raw JSON of Pluggy.
/// </summary>
public sealed record BankAccountsOverviewResponse(IReadOnlyList<BankAccountsPersonResponse> People);

public sealed record BankAccountsPersonResponse(
    Guid UserId,
    string UserName,
    bool IsMe,
    IReadOnlyList<BankBalanceAccountResponse> Accounts,
    IReadOnlyList<BankCardResponse> Cards)
{
    public override string ToString() => nameof(BankAccountsPersonResponse);
}

/// <summary>A checking or savings account. <c>Balance</c> is in <c>Currency</c>, as the bank gave it at <c>BalanceAtUtc</c>.</summary>
public sealed record BankBalanceAccountResponse(
    Guid Id,
    string BankName,
    string Name,
    string Type,
    string? Subtype,
    string? NumberMasked,
    string Currency,
    decimal Balance,
    DateTime BalanceAtUtc);

/// <summary>
/// A credit card. <c>Balance</c> is what Pluggy calls the balance of the card, not a bill. <c>CloseDate</c> and
/// <c>DueDate</c> are the ones the card itself reports. <c>CurrentBill</c>: the first bill due from today (in Brazil)
/// on, null when there is none; <c>NextBills</c>: the ones due after it, nearest first; <c>PreviousBills</c>: the
/// ones already due, most recent first (at most twelve).
/// </summary>
public sealed record BankCardResponse(
    Guid Id,
    string BankName,
    string Name,
    string? Brand,
    string? NumberMasked,
    string Currency,
    decimal Balance,
    DateTime BalanceAtUtc,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateOnly? CloseDate,
    DateOnly? DueDate,
    CardBillResponse? CurrentBill,
    IReadOnlyList<CardBillResponse> NextBills,
    IReadOnlyList<CardBillResponse> PreviousBills);

/// <summary>
/// A bill of a card. <c>TotalAmount</c> and <c>MinimumPayment</c> are in <c>Currency</c>, never converted.
/// <c>IsClosed</c>: the total no longer changes (from the day after the closing date; without a closing date, once
/// the bill is due).
/// </summary>
public sealed record CardBillResponse(
    Guid Id,
    DateOnly DueDate,
    DateOnly? ClosingDate,
    decimal TotalAmount,
    string Currency,
    decimal? MinimumPayment,
    bool IsClosed);

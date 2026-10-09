namespace CoupleSync.Application.OpenFinance;

public sealed record CreateBankConnectionInput(string Label, string ClientId, string ClientSecret, int? HistoryMonths)
{
    /// <summary>Carries credentials: prints none of them (a record would print every property).</summary>
    public override string ToString() => nameof(CreateBankConnectionInput);
}

/// <summary>What the group sees of Open Finance. Never carries a secret nor a whole client id.</summary>
public sealed record OpenFinanceStatusDto(bool Available, IReadOnlyList<BankConnectionDto> Connections);

public sealed record BankConnectionDto(
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
    IReadOnlyList<BankItemDto> Items);

public sealed record BankItemDto(
    Guid Id,
    string ConnectorName,
    string Status,
    string? ExecutionStatus,
    DateTime? LastUpdatedAtUtc,
    string? LastErrorMessage,
    IReadOnlyList<BankAccountDto> Accounts);

public sealed record BankAccountDto(
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

public sealed record SyncRunDto(
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
/// What the review shows of a bank transaction. Never the raw JSON Pluggy sent. <c>Type</c> is <c>Debit</c> (money
/// that left) or <c>Credit</c>; <c>AutoReason</c> is why an ignored line was ignored (<c>Transfer</c> or
/// <c>BillPayment</c>); <c>Match</c> is the transaction of the app a waiting line looks the same as.
/// </summary>
public sealed record BankReviewLineDto(
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
    BankReviewMatchDto? Match);

/// <summary>The transaction of the app a bank line looks the same as. <c>Day</c> is its day in Brazil; <c>Source</c> is how it entered the app.</summary>
public sealed record BankReviewMatchDto(
    Guid TransactionId,
    string Source,
    string? Description,
    string? Merchant,
    decimal Amount,
    string Currency,
    DateOnly Day);

public sealed record BankReviewMonthDto(string Month, int Pending);

/// <summary>How many lines of a month ("yyyy-MM") are automatically ignored.</summary>
public sealed record BankReviewIgnoredMonthDto(string Month, int Ignored);

/// <summary>
/// <c>Ignored</c>: the lines of the month ignored automatically (transfers between accounts of the group, bill
/// payments), money that left and money that came in. <c>SuspectedDuplicates</c>: how many of <c>Expenses</c> look
/// the same as a transaction the app already has. <c>IgnoredAllMonths</c> and <c>IgnoredByMonth</c> (the latest
/// month first): the automatically ignored lines of the group in every month, so that the app can lead to them also
/// when nothing waits for the review. <c>Incomes</c>: the entries (money that came in) of the month
/// waiting; <c>PendingIncomesAllMonths</c> and <c>PendingIncomesByMonth</c> are to them what <c>PendingAllMonths</c>
/// and <c>PendingByMonth</c> are to the expenses. <c>Discarded</c> has the discarded lines of both kinds.
/// </summary>
public sealed record BankReviewDto(
    string Month,
    IReadOnlyList<BankReviewLineDto> Expenses,
    IReadOnlyList<BankReviewLineDto> Discarded,
    decimal PendingTotalBrl,
    int PendingAllMonths,
    IReadOnlyList<BankReviewMonthDto> PendingByMonth,
    IReadOnlyList<BankReviewLineDto> Ignored,
    int SuspectedDuplicates,
    int IgnoredAllMonths,
    IReadOnlyList<BankReviewIgnoredMonthDto> IgnoredByMonth,
    IReadOnlyList<BankReviewLineDto> Incomes,
    int PendingIncomesAllMonths,
    IReadOnlyList<BankReviewMonthDto> PendingIncomesByMonth);

public sealed record ConfirmExpenseInput(Guid Id, string? Category, string? Description);

/// <summary>An entry to register as an income of its month; <c>Name</c> is optional (the description of the line when absent).</summary>
public sealed record ConfirmIncomeInput(Guid Id, string? Name);

/// <summary>An entry that became an income, and that income.</summary>
public sealed record BankReviewIncomeDto(Guid Id, Guid IncomeSourceId);

public sealed record BankReviewCreatedDto(Guid Id, Guid TransactionId);

/// <summary>A line the person said is the same as a transaction the app already had, and that transaction.</summary>
public sealed record BankReviewReconciledDto(Guid Id, Guid TransactionId);

/// <summary>
/// <c>Skipped</c>: lines asked to be confirmed that cannot become an expense (no value, or another currency); they
/// stay waiting (to be discarded). <c>SkippedOtherCurrency</c>: the ones of them skipped for the currency.
/// <c>Reconciled</c>: the lines that now point to the transaction the app already had (nothing was created for them).
/// </summary>
public sealed record BankReviewConfirmResult(
    IReadOnlyList<BankReviewCreatedDto> Created,
    IReadOnlyList<Guid> Discarded,
    int AlreadyConfirmed,
    IReadOnlyList<Guid> Skipped,
    IReadOnlyList<Guid> SkippedOtherCurrency,
    IReadOnlyList<BankReviewReconciledDto> Reconciled,
    IReadOnlyList<BankReviewIncomeDto> Incomes);

// ---------------------------------------------------------------- accounts and bills (GET accounts)

/// <summary>What the group connected, by person: who asks first. Never a credential, a whole number or the raw JSON.</summary>
public sealed record BankAccountsOverviewDto(IReadOnlyList<BankAccountsPersonDto> People);

public sealed record BankAccountsPersonDto(
    Guid UserId,
    string UserName,
    bool IsMe,
    IReadOnlyList<BankBalanceAccountDto> Accounts,
    IReadOnlyList<BankCardDto> Cards)
{
    /// <summary>Carries the name of a person: prints none of it.</summary>
    public override string ToString() => nameof(BankAccountsPersonDto);
}

/// <summary>A checking or savings account. <c>Balance</c> is in <c>Currency</c>, as the bank gave it at <c>BalanceAtUtc</c>.</summary>
public sealed record BankBalanceAccountDto(
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
/// A credit card. <c>Balance</c> is what Pluggy calls the balance of the card (not the bill); the bills are apart:
/// <c>CurrentBill</c> is the first one due from today (in Brazil) on, <c>NextBills</c> the ones due after it (nearest
/// first) and <c>PreviousBills</c> the ones already due (most recent first, at most twelve).
/// </summary>
public sealed record BankCardDto(
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
    CardBillDto? CurrentBill,
    IReadOnlyList<CardBillDto> NextBills,
    IReadOnlyList<CardBillDto> PreviousBills);

/// <summary>A bill of a card. <c>TotalAmount</c> and <c>MinimumPayment</c> are in <c>Currency</c>: never converted.</summary>
public sealed record CardBillDto(
    Guid Id,
    DateOnly DueDate,
    DateOnly? ClosingDate,
    decimal TotalAmount,
    string Currency,
    decimal? MinimumPayment,
    bool IsClosed);

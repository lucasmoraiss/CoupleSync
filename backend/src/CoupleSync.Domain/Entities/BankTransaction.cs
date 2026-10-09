using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Domain.Entities;

public enum BankTransactionType
{
    Debit,
    Credit
}

/// <summary>What the bank says about the transaction: settled, or still to be settled (it may change or vanish).</summary>
public enum BankTransactionStatus
{
    Posted,
    Pending
}

public enum BankTransactionReviewState
{
    /// <summary>Waits for the person.</summary>
    Pending,

    /// <summary>Became a transaction (or an income) of the app.</summary>
    Confirmed,

    /// <summary>The person said it is the same as a transaction the app already had: it points to that one, nothing was created.</summary>
    Reconciled,

    /// <summary>The person discarded it, or deleted the transaction created from it. Can be restored.</summary>
    Discarded,

    /// <summary>Decided automatically, with a reason (<see cref="BankTransactionAutoReasons"/>). Can be restored.</summary>
    Ignored
}

/// <summary>Why a line was ignored without anyone looking at it (stored in <c>auto_reason</c>).</summary>
public static class BankTransactionAutoReasons
{
    /// <summary>Money moving between two accounts of the group.</summary>
    public const string Transfer = "Transfer";

    /// <summary>The payment of a credit card bill: the expenses are the purchases of the card.</summary>
    public const string BillPayment = "BillPayment";
}

/// <summary>Everything Pluggy says about one transaction at a given moment.</summary>
public sealed record BankTransactionSnapshot(
    DateTime DateUtc,
    decimal Amount,
    string? Type,
    string? Currency,
    string? Description,
    string? DescriptionRaw,
    string? PluggyCategory,
    string? PluggyCategoryId,
    string? MerchantName,
    string? MerchantCnpj,
    string? MerchantCategory,
    string? PaymentMethod,
    int? InstallmentNumber,
    int? InstallmentTotal,
    string? BillId,
    string? Status,
    decimal? BalanceAfter,
    string RawJson);

/// <summary>
/// The mirror of one bank transaction as Pluggy delivers it (Open Finance). The review decides what becomes of it;
/// nothing here is ever deleted by the review. A synchronisation refreshes what comes from Pluggy and decides, by
/// the automatic rules, only about lines nobody reviewed (<see cref="OpenToAutomaticRules"/>); it never writes over
/// a review of a person nor over the links.
/// </summary>
public sealed class BankTransaction : ICoupleScoped
{
    public const int MaxPluggyIdLength = 64;
    public const int MaxDescriptionLength = 512;
    public const int MaxCategoryLength = 120;
    public const int MaxCategoryIdLength = 16;
    public const int MaxMerchantNameLength = 256;
    public const int MaxCnpjLength = 32;
    public const int MaxPaymentMethodLength = 32;
    public const int MaxCurrencyLength = 3;
    public const int MaxAutoReasonLength = 16;
    public const int MaxSuggestedCategoryLength = 64;

    private BankTransaction() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }

    /// <summary>Who connected the account: the transactions created from this row are theirs.</summary>
    public Guid UserId { get; private set; }

    public Guid BankAccountId { get; private set; }
    public string PluggyTransactionId { get; private set; } = string.Empty;

    /// <summary>The instant Pluggy gives (UTC).</summary>
    public DateTime Date { get; private set; }

    /// <summary>The calendar day of the transaction in Brazil: what months and days of the review are made of.</summary>
    public DateOnly LocalDate { get; private set; }

    /// <summary>Signed, as Pluggy sends it.</summary>
    public decimal Amount { get; private set; }

    public BankTransactionType Type { get; private set; }
    public string Currency { get; private set; } = CurrencyRules.Brl;
    public string? Description { get; private set; }
    public string? DescriptionRaw { get; private set; }
    public string? PluggyCategory { get; private set; }
    public string? PluggyCategoryId { get; private set; }
    public string? MerchantName { get; private set; }
    public string? MerchantCnpj { get; private set; }
    public string? MerchantCategory { get; private set; }
    public string? PaymentMethod { get; private set; }
    public int? InstallmentNumber { get; private set; }
    public int? InstallmentTotal { get; private set; }
    public string? BillId { get; private set; }
    public BankTransactionStatus Status { get; private set; }
    public decimal? BalanceAfter { get; private set; }

    public BankTransactionReviewState ReviewState { get; private set; }
    public Guid? LinkedTransactionId { get; private set; }

    /// <summary>The income of the month created from this line (money that came in), while it is <c>Confirmed</c>.</summary>
    public Guid? LinkedIncomeSourceId { get; private set; }

    /// <summary>
    /// The transaction of the app this line looks the same as (suspected duplicate). Only while the line waits: the
    /// person says "the same" (<see cref="Reconcile"/>) or "another one" (<see cref="Confirm"/>).
    /// </summary>
    public Guid? MatchedTransactionId { get; private set; }

    /// <summary>One of <see cref="BankTransactionAutoReasons"/>, while the line is <c>Ignored</c>.</summary>
    public string? AutoReason { get; private set; }

    /// <summary>
    /// When the person brought the line back to the review. From then on no automatic rule touches it again: it is
    /// never ignored nor marked as a suspected duplicate by a synchronisation.
    /// </summary>
    public DateTime? PersonDecidedAtUtc { get; private set; }

    /// <summary>A key of <see cref="TransactionCategories"/>, decided when the row first arrived.</summary>
    public string? SuggestedCategory { get; private set; }

    public DateTime? ReviewedAtUtc { get; private set; }

    /// <summary>The transaction exactly as Pluggy sent it. Never logged, never returned by the API.</summary>
    public string RawJson { get; private set; } = "{}";

    /// <summary>The synchronisation that last wrote this row.</summary>
    public Guid? SyncRunId { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Money that left: the only rows the review of expenses shows.</summary>
    public bool IsExpense => Type == BankTransactionType.Debit;

    /// <summary>The value of the expense, always positive.</summary>
    public decimal AbsoluteAmount => Math.Abs(Amount);

    /// <summary>
    /// What can become an expense of the app in this phase: settled at the bank, with a value, in reais. Anything
    /// else waits in the review to be discarded (or, pending at the bank, for the bank to settle it).
    /// </summary>
    public static bool CanBecomeExpense(BankTransactionStatus status, decimal amount, string? currency)
        => status == BankTransactionStatus.Posted && amount != 0 && CurrencyRules.IsBrl(currency);

    /// <summary>
    /// What can become an income of the month: money that came in, settled at the bank, with a value, in reais (the
    /// same conditions of an expense: every sum of the app is in reais).
    /// </summary>
    public static bool CanBecomeIncome(BankTransactionStatus status, decimal amount, string? currency)
        => CanBecomeExpense(status, amount, currency);

    public static BankTransaction Create(
        Guid coupleId,
        Guid userId,
        Guid bankAccountId,
        string pluggyTransactionId,
        BankTransactionSnapshot snapshot,
        string? suggestedCategory,
        Guid syncRunId,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(pluggyTransactionId) || pluggyTransactionId.Trim().Length > MaxPluggyIdLength)
            throw new ArgumentException("O identificador da transação é inválido.", nameof(pluggyTransactionId));

        var transaction = new BankTransaction
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            UserId = userId,
            BankAccountId = bankAccountId,
            PluggyTransactionId = pluggyTransactionId.Trim(),
            ReviewState = BankTransactionReviewState.Pending,
            SuggestedCategory = suggestedCategory,
            CreatedAtUtc = nowUtc,
        };
        transaction.Refresh(snapshot, syncRunId, nowUtc);
        return transaction;
    }

    /// <summary>Takes what Pluggy says now. The review state, the links and the suggestion stay as they are.</summary>
    public void Refresh(BankTransactionSnapshot snapshot, Guid syncRunId, DateTime nowUtc)
    {
        Date = DateTime.SpecifyKind(snapshot.DateUtc, DateTimeKind.Utc);
        LocalDate = LocalDateOf(Date);
        Amount = snapshot.Amount;
        Type = TypeOf(snapshot.Type, snapshot.Amount);
        Currency = TextLimits.Clip(snapshot.Currency, MaxCurrencyLength)?.ToUpperInvariant() ?? CurrencyRules.Brl;
        Description = TextLimits.Clip(snapshot.Description, MaxDescriptionLength);
        DescriptionRaw = TextLimits.Clip(snapshot.DescriptionRaw, MaxDescriptionLength);
        PluggyCategory = TextLimits.Clip(snapshot.PluggyCategory, MaxCategoryLength);
        PluggyCategoryId = TextLimits.Clip(snapshot.PluggyCategoryId, MaxCategoryIdLength);
        MerchantName = TextLimits.Clip(snapshot.MerchantName, MaxMerchantNameLength);
        MerchantCnpj = TextLimits.Clip(snapshot.MerchantCnpj, MaxCnpjLength);
        MerchantCategory = TextLimits.Clip(snapshot.MerchantCategory, MaxCategoryLength);
        PaymentMethod = TextLimits.Clip(snapshot.PaymentMethod, MaxPaymentMethodLength);
        InstallmentNumber = snapshot.InstallmentNumber;
        InstallmentTotal = snapshot.InstallmentTotal;
        BillId = TextLimits.Clip(snapshot.BillId, MaxPluggyIdLength);
        Status = string.Equals(snapshot.Status?.Trim(), "PENDING", StringComparison.OrdinalIgnoreCase)
            ? BankTransactionStatus.Pending
            : BankTransactionStatus.Posted;
        BalanceAfter = snapshot.BalanceAfter;
        RawJson = string.IsNullOrWhiteSpace(snapshot.RawJson) ? "{}" : TextLimits.JsonWithoutNull(snapshot.RawJson);
        SyncRunId = syncRunId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// True while an automatic rule may still decide about the line: it waits for the review and the person never
    /// brought it back to the review.
    /// </summary>
    public bool OpenToAutomaticRules => ReviewState == BankTransactionReviewState.Pending && PersonDecidedAtUtc is null;

    /// <summary>
    /// True when the person said the line is the same as a transaction that is no longer there: it was deleted at the
    /// very moment of the answer, and the store cleared the link. Nothing is left for the line to be the same as.
    /// </summary>
    public bool LostItsTransaction => ReviewState == BankTransactionReviewState.Reconciled && LinkedTransactionId is null;

    /// <summary>
    /// True when the line (money that came in) was registered as an income that is no longer there: it was deleted at
    /// the very moment of the confirmation, and the store cleared the link.
    /// </summary>
    public bool LostItsIncome
        => !IsExpense && ReviewState == BankTransactionReviewState.Confirmed && LinkedIncomeSourceId is null && LinkedTransactionId is null;

    /// <summary>
    /// What the review treats as discarded (listed apart, can be restored): what the person discarded, and a line
    /// that <see cref="LostItsTransaction"/> or <see cref="LostItsIncome"/>.
    /// </summary>
    public bool CountsAsDiscarded => ReviewState == BankTransactionReviewState.Discarded || LostItsTransaction || LostItsIncome;

    /// <summary>
    /// What an automatic rule decided about the line was not stored (the save was refused because someone reviewed
    /// lines at that moment): the line waits again, as it arrived, for the rules to decide again. Never touches what
    /// a person decided.
    /// </summary>
    public void ForgetAutomaticDecision()
    {
        if (PersonDecidedAtUtc is not null) return;
        if (ReviewState is not (BankTransactionReviewState.Pending or BankTransactionReviewState.Ignored)) return;
        ReviewState = BankTransactionReviewState.Pending;
        AutoReason = null;
        MatchedTransactionId = null;
        ReviewedAtUtc = null;
    }

    /// <summary>The row became a transaction of the app (also the answer "it is another one" to a suspected duplicate).</summary>
    public void Confirm(Guid transactionId, DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Confirmed;
        LinkedTransactionId = transactionId;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The row (money that came in) became an income of the month.</summary>
    public void ConfirmAsIncome(Guid incomeSourceId, DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Confirmed;
        LinkedIncomeSourceId = incomeSourceId;
        LinkedTransactionId = null;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The person said the row is the transaction the app already had: it points to that one.</summary>
    public void Reconcile(Guid existingTransactionId, DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Reconciled;
        LinkedTransactionId = existingTransactionId;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Discarded by the person, or the transaction (or the income) it pointed to was deleted: no link stays.</summary>
    public void Discard(DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Discarded;
        LinkedTransactionId = null;
        LinkedIncomeSourceId = null;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Back to the review by the person's hand, from discarded or ignored. It is the person's decision: no
    /// automatic rule decides about this line again (<see cref="PersonDecidedAtUtc"/>).
    /// </summary>
    public void Restore(DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Pending;
        LinkedTransactionId = null;
        LinkedIncomeSourceId = null;
        MatchedTransactionId = null;
        AutoReason = null;
        ReviewedAtUtc = null;
        PersonDecidedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// An automatic rule decided the line is neither an expense nor an income (<paramref name="reason"/> is one of
    /// <see cref="BankTransactionAutoReasons"/>). Does nothing, and says so, when the line is not
    /// <see cref="OpenToAutomaticRules"/>.
    /// </summary>
    public bool IgnoreAutomatically(string reason, DateTime nowUtc)
    {
        if (!OpenToAutomaticRules) return false;
        ReviewState = BankTransactionReviewState.Ignored;
        AutoReason = reason;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// True while the line can still be one leg of a transfer between accounts of the group: it is open to the rules,
    /// or it was ignored as a bill payment before its other leg (the credit on the card) arrived.
    /// </summary>
    public bool CanBePairedAsTransfer
        => OpenToAutomaticRules
           || (ReviewState == BankTransactionReviewState.Ignored && AutoReason == BankTransactionAutoReasons.BillPayment);

    /// <summary>
    /// The other leg of this line arrived: both are one transfer between accounts of the group. Does nothing, and
    /// says so, when the line cannot be paired (<see cref="CanBePairedAsTransfer"/>).
    /// </summary>
    public bool PairAsTransfer(DateTime nowUtc)
    {
        if (!CanBePairedAsTransfer) return false;
        ReviewState = BankTransactionReviewState.Ignored;
        AutoReason = BankTransactionAutoReasons.Transfer;
        MatchedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// The line stays waiting, pointing to the transaction it looks the same as. Does nothing, and says so, when the
    /// line is not <see cref="OpenToAutomaticRules"/>.
    /// </summary>
    public bool MarkSuspectedDuplicate(Guid transactionId, DateTime nowUtc)
    {
        if (!OpenToAutomaticRules) return false;
        MatchedTransactionId = transactionId;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// The instant to give the transaction created from this row: the one Pluggy gave, unless that instant falls on
    /// another day in Brazil than <see cref="LocalDate"/> (a date sent without a time), when it is noon of the local day.
    /// </summary>
    public DateTime EventTimestampUtc()
        => DateOnly.FromDateTime(BrazilTime.ToLocal(Date)) == LocalDate
            ? Date
            : BrazilTime.ToUtc(LocalDate.ToDateTime(new TimeOnly(12, 0)));

    /// <summary>
    /// The day of the transaction in Brazil. Banks that give no time send midnight UTC ("2026-10-05T00:00:00.000Z"):
    /// that is the day as written, not 21:00 of the day before in Brasília.
    /// </summary>
    public static DateOnly LocalDateOf(DateTime dateUtc)
        => dateUtc.TimeOfDay == TimeSpan.Zero
            ? DateOnly.FromDateTime(dateUtc)
            : DateOnly.FromDateTime(BrazilTime.ToLocal(dateUtc));

    /// <summary>The type Pluggy gives; without one, the sign decides (a negative amount is money leaving the account).</summary>
    public static BankTransactionType TypeOf(string? type, decimal amount)
    {
        if (string.Equals(type?.Trim(), "CREDIT", StringComparison.OrdinalIgnoreCase)) return BankTransactionType.Credit;
        if (string.Equals(type?.Trim(), "DEBIT", StringComparison.OrdinalIgnoreCase)) return BankTransactionType.Debit;
        return amount < 0 ? BankTransactionType.Debit : BankTransactionType.Credit;
    }
}

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

    /// <summary>Was the same as a record the app already had (phase 3).</summary>
    Reconciled,

    /// <summary>The person discarded it, or deleted the transaction created from it. Can be restored.</summary>
    Discarded,

    /// <summary>Decided automatically (phase 3). Can be restored.</summary>
    Ignored
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
/// nothing here is ever deleted by the review. A synchronisation refreshes only what comes from Pluggy, never
/// <see cref="ReviewState"/> nor the links.
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

    /// <summary>Phase 4 (incomes). Never written yet.</summary>
    public Guid? LinkedIncomeSourceId { get; private set; }

    /// <summary>Phase 3 (suspected collision). Never written yet.</summary>
    public Guid? MatchedTransactionId { get; private set; }

    /// <summary>Phase 3 (<c>Transfer</c> / <c>BillPayment</c>). Never written yet.</summary>
    public string? AutoReason { get; private set; }

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
        RawJson = string.IsNullOrWhiteSpace(snapshot.RawJson) ? "{}" : snapshot.RawJson;
        SyncRunId = syncRunId;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The row became a transaction of the app.</summary>
    public void Confirm(Guid transactionId, DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Confirmed;
        LinkedTransactionId = transactionId;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Discarded by the person, or the transaction created from it was deleted: no link stays.</summary>
    public void Discard(DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Discarded;
        LinkedTransactionId = null;
        ReviewedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Back to the review, as if it had just arrived.</summary>
    public void Restore(DateTime nowUtc)
    {
        ReviewState = BankTransactionReviewState.Pending;
        LinkedTransactionId = null;
        ReviewedAtUtc = null;
        UpdatedAtUtc = nowUtc;
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

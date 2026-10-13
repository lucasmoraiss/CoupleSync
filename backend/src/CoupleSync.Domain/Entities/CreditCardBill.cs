using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Domain.Entities;

/// <summary>What Pluggy says about one credit card bill at a given moment.</summary>
public sealed record CreditCardBillSnapshot(
    DateOnly DueDate,
    DateOnly? ClosingDate,
    decimal TotalAmount,
    string? Currency,
    decimal? MinimumPayment,
    string RawJson)
{
    /// <summary>Carries the raw JSON of the bill: prints none of it.</summary>
    public override string ToString() => nameof(CreditCardBillSnapshot);
}

/// <summary>
/// The mirror of one bill of a credit card (<see cref="BankAccount"/> of type CREDIT) as Pluggy lists it. Dates are
/// calendar days, as written by the bank. Money is never converted: <see cref="TotalAmount"/> is in
/// <see cref="Currency"/>, and only bills in reais enter a sum in reais.
/// </summary>
public sealed class CreditCardBill : ICoupleScoped
{
    public const int MaxPluggyIdLength = 64;
    public const int MaxCurrencyLength = 3;

    private CreditCardBill() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid BankAccountId { get; private set; }
    public string PluggyBillId { get; private set; } = string.Empty;
    public DateOnly DueDate { get; private set; }

    /// <summary>The day the bill closes (closed), when the bank says it.</summary>
    public DateOnly? ClosingDate { get; private set; }

    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = CurrencyRules.Brl;
    public decimal? MinimumPayment { get; private set; }

    /// <summary>
    /// Whether the bill was closed (its total no longer changes) on the day of the synchronisation that last wrote
    /// it: see <see cref="IsClosedOn"/>.
    /// </summary>
    public bool IsClosed { get; private set; }

    /// <summary>The bill exactly as Pluggy sent it. Never logged, never returned by the API.</summary>
    public string RawJson { get; private set; } = "{}";

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <param name="accountCurrency">The currency of the card: the one of a bill that names none.</param>
    /// <param name="todayInBrazil">The calendar day in Brazil at the moment of the synchronisation.</param>
    public static CreditCardBill Create(
        Guid coupleId,
        Guid bankAccountId,
        string pluggyBillId,
        CreditCardBillSnapshot snapshot,
        string? accountCurrency,
        DateOnly todayInBrazil,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(pluggyBillId) || pluggyBillId.Trim().Length > MaxPluggyIdLength)
            throw new ArgumentException("O identificador da fatura é inválido.", nameof(pluggyBillId));

        var bill = new CreditCardBill
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            BankAccountId = bankAccountId,
            PluggyBillId = pluggyBillId.Trim(),
            CreatedAtUtc = nowUtc,
        };
        bill.Refresh(snapshot, accountCurrency, todayInBrazil, nowUtc);
        return bill;
    }

    /// <summary>
    /// Takes what Pluggy says about the bill now. True when something the payment of a bill is recognised by changed
    /// (the total, the currency, the due date, or whether it is closed).
    /// </summary>
    public bool Refresh(CreditCardBillSnapshot snapshot, string? accountCurrency, DateOnly todayInBrazil, DateTime nowUtc)
    {
        var before = (TotalAmount, Currency, DueDate, IsClosed);

        DueDate = snapshot.DueDate;
        ClosingDate = snapshot.ClosingDate;
        TotalAmount = snapshot.TotalAmount;
        Currency = (TextLimits.Clip(snapshot.Currency, MaxCurrencyLength)
                    ?? TextLimits.Clip(accountCurrency, MaxCurrencyLength)
                    ?? CurrencyRules.Brl).ToUpperInvariant();
        MinimumPayment = snapshot.MinimumPayment;
        IsClosed = IsClosedOn(snapshot.ClosingDate, snapshot.DueDate, todayInBrazil);
        RawJson = string.IsNullOrWhiteSpace(snapshot.RawJson) ? "{}" : TextLimits.JsonWithoutNull(snapshot.RawJson);
        UpdatedAtUtc = nowUtc;

        return before != (TotalAmount, Currency, DueDate, IsClosed);
    }

    /// <summary>
    /// Pluggy gives no "closed" flag. A bill is taken as closed only when that is certain: from the day AFTER its
    /// closing date on (on the closing day itself a purchase may still enter); without a closing date, only once it
    /// is due. In doubt the bill is open: an open bill never makes a debit be ignored as its payment.
    /// </summary>
    public static bool IsClosedOn(DateOnly? closingDate, DateOnly dueDate, DateOnly today)
        => closingDate is { } closing ? closing < today : dueDate <= today;
}

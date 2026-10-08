using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

/// <summary>What Pluggy says about one account or card at a given moment.</summary>
public sealed record BankAccountSnapshot(
    string Type,
    string? Subtype,
    string Name,
    string? MarketingName,
    string? Number,
    string? Currency,
    decimal Balance,
    decimal? CreditLimit,
    decimal? AvailableCreditLimit,
    DateOnly? BalanceCloseDate,
    DateOnly? BalanceDueDate,
    decimal? MinimumPayment,
    string? Brand);

/// <summary>A checking/savings account or a credit card of a <see cref="BankItem"/>.</summary>
public sealed class BankAccount : ICoupleScoped
{
    public const int MaxPluggyIdLength = 64;
    public const int MaxTypeLength = 32;
    public const int MaxNameLength = 160;
    public const int MaxCurrencyLength = 8;
    public const int MaxBrandLength = 32;
    public const int MaskedNumberLength = 4;
    public const string DefaultCurrency = "BRL";

    private BankAccount() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid ItemId { get; private set; }
    public string PluggyAccountId { get; private set; } = string.Empty;

    /// <summary>BANK or CREDIT, as Pluggy sends it.</summary>
    public string Type { get; private set; } = string.Empty;

    /// <summary>CHECKING_ACCOUNT, SAVINGS_ACCOUNT or CREDIT_CARD, as Pluggy sends it.</summary>
    public string? Subtype { get; private set; }

    public string Name { get; private set; } = string.Empty;
    public string? MarketingName { get; private set; }

    /// <summary>Only the last characters of the account/card number; the full number is never stored.</summary>
    public string? NumberMasked { get; private set; }

    public string Currency { get; private set; } = DefaultCurrency;
    public decimal Balance { get; private set; }
    public DateTime BalanceAtUtc { get; private set; }

    public decimal? CreditLimit { get; private set; }
    public decimal? AvailableCreditLimit { get; private set; }
    public DateOnly? BalanceCloseDate { get; private set; }
    public DateOnly? BalanceDueDate { get; private set; }
    public decimal? MinimumPayment { get; private set; }
    public string? Brand { get; private set; }

    public bool SyncEnabled { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static BankAccount Create(Guid coupleId, Guid itemId, string pluggyAccountId, BankAccountSnapshot snapshot, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(pluggyAccountId) || pluggyAccountId.Trim().Length > MaxPluggyIdLength)
            throw new ArgumentException("O identificador da conta é inválido.", nameof(pluggyAccountId));

        var account = new BankAccount
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            ItemId = itemId,
            PluggyAccountId = pluggyAccountId.Trim(),
            SyncEnabled = true,
        };
        account.Refresh(snapshot, nowUtc);
        return account;
    }

    /// <summary>Takes what Pluggy says about the account now. The person's choice of <see cref="SyncEnabled"/> stays.</summary>
    public void Refresh(BankAccountSnapshot snapshot, DateTime nowUtc)
    {
        Type = TextLimits.Clip(snapshot.Type, MaxTypeLength) ?? string.Empty;
        Subtype = TextLimits.Clip(snapshot.Subtype, MaxTypeLength);
        Name = TextLimits.Clip(snapshot.Name, MaxNameLength) ?? string.Empty;
        MarketingName = TextLimits.Clip(snapshot.MarketingName, MaxNameLength);
        NumberMasked = MaskNumber(snapshot.Number);
        Currency = TextLimits.Clip(snapshot.Currency, MaxCurrencyLength)?.ToUpperInvariant() ?? DefaultCurrency;
        Balance = snapshot.Balance;
        BalanceAtUtc = nowUtc;
        CreditLimit = snapshot.CreditLimit;
        AvailableCreditLimit = snapshot.AvailableCreditLimit;
        BalanceCloseDate = snapshot.BalanceCloseDate;
        BalanceDueDate = snapshot.BalanceDueDate;
        MinimumPayment = snapshot.MinimumPayment;
        Brand = TextLimits.Clip(snapshot.Brand, MaxBrandLength);
        UpdatedAtUtc = nowUtc;
    }

    public void SetSyncEnabled(bool enabled, DateTime nowUtc)
    {
        SyncEnabled = enabled;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>The last 4 letters/digits of a number ("0001/98765-1234" → "1234"); null when there is none.</summary>
    public static string? MaskNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var significant = number.Where(char.IsAsciiLetterOrDigit).ToArray();
        if (significant.Length == 0) return null;
        return new string(significant.Length <= MaskedNumberLength ? significant : significant[^MaskedNumberLength..]);
    }
}

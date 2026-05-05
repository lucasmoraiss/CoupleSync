using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed partial class IncomeSource : ICoupleScoped
{
    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthFormatRegex();

    private IncomeSource() { }

    private IncomeSource(
        Guid id,
        Guid coupleId,
        Guid userId,
        string month,
        string name,
        decimal amount,
        string currency,
        bool isShared,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        UserId = userId;
        Month = month;
        Name = name;
        Amount = amount;
        Currency = currency;
        IsShared = isShared;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }
    public string Month { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "BRL";
    public bool IsShared { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public Couple Couple { get; private set; } = null!;

    public static IncomeSource Create(
        Guid coupleId,
        Guid userId,
        string month,
        string name,
        decimal amount,
        string currency,
        bool isShared,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(month) || month.Length != 7 || !MonthFormatRegex().IsMatch(month))
            throw new ArgumentException("Month must be in YYYY-MM format (exactly 7 characters).", nameof(month));

        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
            throw new ArgumentException("Name must be a non-empty string of at most 64 characters.", nameof(name));

        if (amount < 0)
            throw new ArgumentException("Amount must be zero or greater.", nameof(amount));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length < 2 || currency.Length > 3)
            throw new ArgumentException("Currency must be 2-3 characters.", nameof(currency));

        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        return new IncomeSource(Guid.NewGuid(), coupleId, userId, month, name.Trim(), amount, currency, isShared, createdAtUtc);
    }

    public void Update(string? name, decimal? amount, bool? isShared, DateTime nowUtc)
    {
        if (name is not null)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
                throw new ArgumentException("Name must be a non-empty string of at most 64 characters.", nameof(name));
            Name = name.Trim();
        }

        if (amount is not null)
        {
            if (amount.Value < 0)
                throw new ArgumentException("Amount must be zero or greater.", nameof(amount));
            Amount = amount.Value;
        }

        if (isShared is not null)
        {
            IsShared = isShared.Value;
        }

        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Returns true if the given user can modify this income source (owner or shared).</summary>
    public bool CanBeEditedBy(Guid userId)
        => UserId == userId || IsShared;
}

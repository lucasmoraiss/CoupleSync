using System.Text.RegularExpressions;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed partial class BudgetPlan : ICoupleScoped
{
    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$")]
    private static partial Regex MonthFormatRegex();
    private BudgetPlan() { }

    private BudgetPlan(
        Guid id,
        Guid coupleId,
        string month,
        decimal grossIncome,
        string currency,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        Month = month;
        GrossIncome = grossIncome;
        Currency = currency;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public string Month { get; private set; } = string.Empty;
    public decimal GrossIncome { get; private set; }
    public string Currency { get; private set; } = "BRL";
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public Couple Couple { get; private set; } = null!; // Navigation needed for EF FK config
    public ICollection<BudgetAllocation> Allocations { get; private set; } = new List<BudgetAllocation>();

    public static BudgetPlan Create(
        Guid coupleId,
        string month,
        decimal grossIncome,
        string currency,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(month) || month.Length != 7 || !MonthFormatRegex().IsMatch(month))
            throw new ArgumentException("Month must be in YYYY-MM format (exactly 7 characters).", nameof(month));

        if (grossIncome < 0)
            throw new ArgumentException("GrossIncome must be zero or greater.", nameof(grossIncome));

        if (string.IsNullOrWhiteSpace(currency) || currency.Length < 2 || currency.Length > 3)
            throw new ArgumentException("Currency must be 2-3 characters.", nameof(currency));

        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        return new BudgetPlan(Guid.NewGuid(), coupleId, month, grossIncome, currency, createdAtUtc);
    }

    public void Update(decimal? grossIncome, string? currency, DateTime nowUtc)
    {
        if (grossIncome is not null)
        {
            if (grossIncome.Value < 0)
                throw new ArgumentException("GrossIncome must be zero or greater.", nameof(grossIncome));
            GrossIncome = grossIncome.Value;
        }

        if (currency is not null)
        {
            if (string.IsNullOrWhiteSpace(currency) || currency.Length < 2 || currency.Length > 3)
                throw new ArgumentException("Currency must be 2-3 characters.", nameof(currency));
            Currency = currency;
        }

        UpdatedAtUtc = nowUtc;
    }
}

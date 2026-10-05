namespace CoupleSync.Domain.ValueObjects;

/// <summary>
/// Single source of truth for monetary bounds accepted at the API edge.
/// Every monetary column is numeric(18,2) (up to 16 integer digits); the ceiling below keeps
/// 12 integer digits so that individual values and their aggregates always fit the column.
/// </summary>
public static class MoneyRules
{
    /// <summary>Highest monetary value accepted from clients: 999,999,999,999.99.</summary>
    public const decimal MaxAmount = 999_999_999_999.99m;

    /// <summary>Smallest strictly positive value representable with two decimal places.</summary>
    public const decimal MinPositiveAmount = 0.01m;

    /// <summary>True when the value has no more than two decimal places (10.5 and 10.50 pass, 10.505 fails).</summary>
    public static bool HasAtMostTwoDecimals(decimal value) => decimal.Round(value, 2) == value;
}

namespace CoupleSync.Application.Reports;

/// <summary>Internal query result rows — not exposed over the wire.</summary>
public sealed record CategorySpendingRow(string Category, decimal Total);

public sealed record MonthlySpendingRow(int Year, int Month, decimal Total);

public sealed record CategorySpendingItem(string Name, decimal Total, decimal Percentage, string Color);

public sealed record SpendingByCategoryResult(IReadOnlyList<CategorySpendingItem> Categories);

public sealed record MonthlyTrendItem(string Month, decimal Income, decimal Expense, decimal Net);

public sealed record MonthlyTrendsResult(IReadOnlyList<MonthlyTrendItem> Months);

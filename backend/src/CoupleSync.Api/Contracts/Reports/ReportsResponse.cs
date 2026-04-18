namespace CoupleSync.Api.Contracts.Reports;

public sealed record CategorySpendingItemResponse(
    string Name,
    decimal Total,
    decimal Percentage,
    string Color);

public sealed record SpendingByCategoryResponse(IReadOnlyList<CategorySpendingItemResponse> Categories);

public sealed record MonthlyTrendItemResponse(
    string Month,
    decimal Income,
    decimal Expense,
    decimal Net);

public sealed record MonthlyTrendsResponse(IReadOnlyList<MonthlyTrendItemResponse> Months);

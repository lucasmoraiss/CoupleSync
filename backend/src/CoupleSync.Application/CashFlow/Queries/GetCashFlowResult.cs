namespace CoupleSync.Application.CashFlow.Queries;

public sealed record GetCashFlowResult(
    int Horizon,
    DateTime HistoricalPeriodStart,
    DateTime HistoricalPeriodEnd,
    int TransactionCount,
    decimal TotalHistoricalSpend,
    decimal AverageDailySpend,
    /// <summary>Spend expected for the whole current month: spent so far plus the forecast for the remaining days.</summary>
    decimal ProjectedSpend,
    IReadOnlyDictionary<string, decimal> CategoryBreakdown,
    string Assumptions,
    DateTime GeneratedAtUtc,
    string Month,
    decimal MonthIncome,
    decimal MonthSpentToDate,
    decimal ForecastDailyAverage,
    int RemainingDays,
    decimal ForecastRemainingSpend,
    decimal ProjectedMonthEndBalance);

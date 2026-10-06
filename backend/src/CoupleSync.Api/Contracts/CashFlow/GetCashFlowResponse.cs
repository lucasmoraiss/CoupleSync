namespace CoupleSync.Api.Contracts.CashFlow;

public sealed record GetCashFlowResponse(
    int Horizon,
    DateTime HistoricalPeriodStart,
    DateTime HistoricalPeriodEnd,
    int TransactionCount,
    decimal TotalHistoricalSpend,
    decimal AverageDailySpend,
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

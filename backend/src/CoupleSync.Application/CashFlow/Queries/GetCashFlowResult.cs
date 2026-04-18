namespace CoupleSync.Application.CashFlow.Queries;

public sealed record GetCashFlowResult(
    int Horizon,
    DateTime HistoricalPeriodStart,
    DateTime HistoricalPeriodEnd,
    int TransactionCount,
    decimal TotalHistoricalSpend,
    decimal AverageDailySpend,
    decimal ProjectedSpend,
    IReadOnlyDictionary<string, decimal> CategoryBreakdown,
    string Assumptions,
    DateTime GeneratedAtUtc);

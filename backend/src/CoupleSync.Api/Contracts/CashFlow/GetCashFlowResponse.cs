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
    DateTime GeneratedAtUtc);

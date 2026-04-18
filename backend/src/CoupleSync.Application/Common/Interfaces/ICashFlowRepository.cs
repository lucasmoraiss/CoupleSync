namespace CoupleSync.Application.Common.Interfaces;

public sealed record CashFlowData(
    int TransactionCount,
    decimal TotalSpend,
    IReadOnlyDictionary<string, decimal> CategoryBreakdown);

public interface ICashFlowRepository
{
    Task<CashFlowData> GetHistoricalDataAsync(Guid coupleId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.CashFlow.Queries;

public sealed class GetCashFlowQueryHandler
{
    private readonly ICashFlowRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetCashFlowQueryHandler(ICashFlowRepository repository, IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GetCashFlowResult> HandleAsync(GetCashFlowQuery query, CancellationToken cancellationToken)
    {
        if (query.Horizon != 30 && query.Horizon != 90)
            throw new ArgumentException($"Horizon must be 30 or 90, got {query.Horizon}.", nameof(query));

        var nowUtc = _dateTimeProvider.UtcNow;
        var fromUtc = nowUtc.AddDays(-query.Horizon);
        var toUtc = nowUtc;

        var data = await _repository.GetHistoricalDataAsync(query.CoupleId, fromUtc, toUtc, cancellationToken);

        decimal averageDailySpend = 0;
        decimal projectedSpend = 0;

        if (data.TransactionCount > 0)
        {
            averageDailySpend = data.TotalSpend / query.Horizon;
            projectedSpend = averageDailySpend * query.Horizon;
        }

        var assumptions = $"Based on {data.TransactionCount} transactions over the last {query.Horizon} days";

        return new GetCashFlowResult(
            query.Horizon,
            fromUtc,
            toUtc,
            data.TransactionCount,
            data.TotalSpend,
            averageDailySpend,
            projectedSpend,
            data.CategoryBreakdown,
            assumptions,
            nowUtc);
    }
}

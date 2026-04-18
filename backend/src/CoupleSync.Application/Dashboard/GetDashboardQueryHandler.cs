using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Dashboard;

public sealed class GetDashboardQueryHandler
{
    private readonly IDashboardRepository _repository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public GetDashboardQueryHandler(IDashboardRepository repository, IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GetDashboardResult> HandleAsync(GetDashboardQuery query, CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var periodStart = query.StartDate
            ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodEnd = query.EndDate
            ?? new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month), 23, 59, 59, DateTimeKind.Utc);

        if (periodStart > periodEnd)
            throw new ArgumentException("INVALID_DATE_RANGE: startDate must not be after endDate.", nameof(query));

        var aggregates = await _repository.GetAggregatesAsync(query.CoupleId, periodStart, periodEnd, cancellationToken);

        return new GetDashboardResult(
            aggregates.TotalExpenses,
            aggregates.ExpensesByCategory,
            aggregates.PartnerBreakdown,
            aggregates.TransactionCount,
            periodStart,
            periodEnd,
            now);
    }
}

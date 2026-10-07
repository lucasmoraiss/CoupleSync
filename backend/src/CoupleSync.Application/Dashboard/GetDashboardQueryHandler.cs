using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.ValueObjects;

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
        var (currentMonthStartUtc, nextMonthStartUtc) = BrazilTime.MonthRangeUtc(BrazilTime.MonthOf(now));
        var periodStart = ResolveStart(query.StartDate) ?? currentMonthStartUtc;
        var periodEnd = ResolveEnd(query.EndDate) ?? nextMonthStartUtc.AddMilliseconds(-1);

        if (periodStart > periodEnd)
            throw new BadRequestException("INVALID_DATE_RANGE", "A data inicial não pode ser posterior à data final.");

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

    // A date without a time zone is a calendar day in Brasília; an instant (Kind Utc) is used as is.
    private static DateTime? ResolveStart(DateTime? start)
    {
        if (start is null) return null;
        return start.Value.Kind == DateTimeKind.Unspecified ? BrazilTime.ToUtc(start.Value) : start.Value;
    }

    // A date-only end (midnight) is inclusive: it runs to the end of that day.
    private static DateTime? ResolveEnd(DateTime? end)
    {
        if (end is null) return null;
        var value = end.Value;
        if (value.Kind == DateTimeKind.Unspecified)
            return value.TimeOfDay == TimeSpan.Zero
                ? BrazilTime.ToUtc(value.Date.AddDays(1)).AddMilliseconds(-1)
                : BrazilTime.ToUtc(value);

        return value.TimeOfDay == TimeSpan.Zero
            ? DateTime.SpecifyKind(value.Date.AddDays(1).AddMilliseconds(-1), DateTimeKind.Utc)
            : value;
    }
}

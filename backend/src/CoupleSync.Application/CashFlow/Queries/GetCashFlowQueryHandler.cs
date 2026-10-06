using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Income;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.CashFlow.Queries;

public sealed class GetCashFlowQueryHandler
{
    private readonly ICashFlowRepository _repository;
    private readonly IIncomeSourceRepository _incomeRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    // Fewer elapsed days than this is too little to extrapolate from; the previous month's pace is used.
    private const int MinDaysForCurrentPace = 3;

    public GetCashFlowQueryHandler(
        ICashFlowRepository repository,
        IIncomeSourceRepository incomeRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _incomeRepository = incomeRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<GetCashFlowResult> HandleAsync(GetCashFlowQuery query, CancellationToken cancellationToken)
    {
        if (query.Horizon != 30 && query.Horizon != 90)
            throw new BadRequestException("INVALID_HORIZON", "O horizonte deve ser 30 ou 90 dias.");

        var nowUtc = _dateTimeProvider.UtcNow;
        var fromUtc = nowUtc.AddDays(-query.Horizon);
        var toUtc = nowUtc;

        var data = await _repository.GetHistoricalDataAsync(query.CoupleId, fromUtc, toUtc, cancellationToken);

        decimal averageDailySpend = 0;

        if (data.TransactionCount > 0)
            averageDailySpend = data.TotalSpend / query.Horizon;

        // Forecast for the current Brasília month: income − spent so far − expected spend of the remaining days.
        var month = BrazilTime.MonthOf(nowUtc);
        var local = BrazilTime.ToLocal(nowUtc);
        var daysInMonth = DateTime.DaysInMonth(local.Year, local.Month);
        var daysElapsed = local.Day;
        var remainingDays = daysInMonth - daysElapsed;

        var monthStartUtc = BrazilTime.MonthRangeUtc(month).StartUtc;
        var monthData = await _repository.GetHistoricalDataAsync(query.CoupleId, monthStartUtc, nowUtc, cancellationToken);

        decimal forecastDailyAverage;
        string paceSource;
        if (daysElapsed >= MinDaysForCurrentPace)
        {
            forecastDailyAverage = monthData.TotalSpend / daysElapsed;
            paceSource = "média diária do mês atual";
        }
        else
        {
            var previousMonth = BrazilTime.AddMonths(month, -1);
            var (previousStartUtc, previousEndUtc) = BrazilTime.MonthRangeUtc(previousMonth);
            var previousData = await _repository.GetHistoricalDataAsync(
                query.CoupleId, previousStartUtc, previousEndUtc.AddTicks(-1), cancellationToken);
            var daysInPreviousMonth = DateTime.DaysInMonth(
                int.Parse(previousMonth[..4]), int.Parse(previousMonth[5..]));
            forecastDailyAverage = previousData.TransactionCount > 0
                ? previousData.TotalSpend / daysInPreviousMonth
                : 0m;
            paceSource = "média diária do mês anterior (poucos dias de dados no mês atual)";
        }

        var forecastRemainingSpend = forecastDailyAverage * remainingDays;
        var candidates = await _incomeRepository.GetCandidatesForMonthsAsync(query.CoupleId, month, month, cancellationToken);
        var monthIncome = IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(candidates, month));
        // Spend expected by the end of the month (the app's "Projeção" figure); TotalHistoricalSpend keeps the raw total.
        var projectedSpend = monthData.TotalSpend + forecastRemainingSpend;
        var projectedMonthEndBalance = monthIncome - monthData.TotalSpend - forecastRemainingSpend;

        var assumptions = $"Projeção do saldo no fim do mês: renda do mês menos o gasto até hoje menos o gasto previsto para os {remainingDays} dias restantes ({paceSource}). Gasto histórico baseado em {data.TransactionCount} transações nos últimos {query.Horizon} dias.";

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
            nowUtc,
            month,
            monthIncome,
            monthData.TotalSpend,
            forecastDailyAverage,
            remainingDays,
            forecastRemainingSpend,
            projectedMonthEndBalance);
    }
}

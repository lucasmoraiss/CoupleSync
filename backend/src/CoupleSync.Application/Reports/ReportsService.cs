using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Income;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.Application.Reports;

public sealed class ReportsService
{
    // Deterministic color palette for category slices (cycles if > 12 categories).
    private static readonly string[] Palette =
    [
        "#6366F1", "#F59E0B", "#22C55E", "#EF4444", "#3B82F6",
        "#A855F7", "#EC4899", "#14B8A6", "#F97316", "#84CC16",
        "#06B6D4", "#F43F5E",
    ];

    private readonly IReportsRepository _repository;
    private readonly IIncomeSourceRepository _incomeRepository;
    private readonly IDateTimeProvider _dateTimeProvider;

    public ReportsService(
        IReportsRepository repository,
        IIncomeSourceRepository incomeRepository,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _incomeRepository = incomeRepository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(
        Guid coupleId,
        int months,
        CancellationToken ct)
    {
        if (months is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(months), "O número de meses deve estar entre 1 e 60.");

        var now = _dateTimeProvider.UtcNow;
        var currentMonth = BrazilTime.MonthOf(now);
        var firstMonth = BrazilTime.AddMonths(currentMonth, -months + 1);
        var from = BrazilTime.MonthRangeUtc(firstMonth).StartUtc;

        var rows = await _repository.GetSpendingByCategoryAsync(coupleId, from, now, ct);

        var grandTotal = rows.Sum(r => r.Total);

        var items = rows
            .OrderByDescending(r => r.Total)
            .Select((r, i) => new CategorySpendingItem(
                r.Category,
                r.Total,
                grandTotal == 0 ? 0m : Math.Round(r.Total / grandTotal * 100, 2),
                Palette[i % Palette.Length]))
            .ToList();

        return new SpendingByCategoryResult(items);
    }

    public async Task<MonthlyTrendsResult> GetMonthlyTrendsAsync(
        Guid coupleId,
        int months,
        CancellationToken ct)
    {
        if (months is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(months), "O número de meses deve estar entre 1 e 60.");

        var now = _dateTimeProvider.UtcNow;
        var currentMonth = BrazilTime.MonthOf(now);
        var firstMonth = BrazilTime.AddMonths(currentMonth, -months + 1);
        var from = BrazilTime.MonthRangeUtc(firstMonth).StartUtc;

        var rows = await _repository.GetMonthlySpendingAsync(coupleId, from, now, ct);
        var lookup = rows.ToDictionary(r => $"{r.Year:D4}-{r.Month:D2}", r => r.Total);

        // Real income of the group: registered sources of every member, recurring ones carried forward.
        var candidates = await _incomeRepository.GetCandidatesForMonthsAsync(coupleId, firstMonth, currentMonth, ct);

        // Build a full calendar of N months so gaps show as 0.
        var items = new List<MonthlyTrendItem>(months);
        for (var i = 0; i < months; i++)
        {
            var month = BrazilTime.AddMonths(firstMonth, i);
            var expense = lookup.TryGetValue(month, out var v) ? v : 0m;
            var income = IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(candidates, month));
            items.Add(new MonthlyTrendItem(month, income, expense, income - expense));
        }

        return new MonthlyTrendsResult(items);
    }
}

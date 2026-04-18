using CoupleSync.Application.Common.Interfaces;

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
    private readonly IDateTimeProvider _dateTimeProvider;

    public ReportsService(IReportsRepository repository, IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<SpendingByCategoryResult> GetSpendingByCategoryAsync(
        Guid coupleId,
        int months,
        CancellationToken ct)
    {
        if (months is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(months), "months must be between 1 and 60.");

        var now = _dateTimeProvider.UtcNow;
        var from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-months + 1);

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
            throw new ArgumentOutOfRangeException(nameof(months), "months must be between 1 and 60.");

        var now = _dateTimeProvider.UtcNow;
        var from = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-months + 1);

        var rows = await _repository.GetMonthlySpendingAsync(coupleId, from, now, ct);
        var lookup = rows.ToDictionary(r => (r.Year, r.Month), r => r.Total);

        // Build a full calendar of N months so gaps show as 0.
        var items = new List<MonthlyTrendItem>(months);
        for (var i = 0; i < months; i++)
        {
            var d = from.AddMonths(i);
            var expense = lookup.TryGetValue((d.Year, d.Month), out var v) ? v : 0m;
            items.Add(new MonthlyTrendItem(
                $"{d.Year:D4}-{d.Month:D2}",
                Income: 0m,   // V1 — income not tracked at transaction level
                Expense: expense,
                Net: -expense));
        }

        return new MonthlyTrendsResult(items);
    }
}

using CoupleSync.Application.CashFlow.Queries;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.CashFlow;

public sealed class GetCashFlowQueryHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private static GetCashFlowQueryHandler BuildHandler(
        FakeCashFlowRepository repo,
        FakeIncomeSourceRepository? incomes = null,
        DateTime? now = null)
        => new(repo, incomes ?? new FakeIncomeSourceRepository(), new FixedDateTimeProvider(now ?? FixedNow));

    [Fact]
    public async Task Horizon30_WithTransactions_ReturnsCorrectProjectedSpend()
    {
        var repo = new FakeCashFlowRepository();
        var coupleId = Guid.NewGuid();

        // 3 transactions totalling 300, spread within last 30 days
        repo.AddTransaction(coupleId, 100m, "Alimentacao", FixedNow.AddDays(-5));
        repo.AddTransaction(coupleId, 100m, "Transporte", FixedNow.AddDays(-10));
        repo.AddTransaction(coupleId, 100m, "Alimentacao", FixedNow.AddDays(-20));

        var handler = BuildHandler(repo);
        var result = await handler.HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal(30, result.Horizon);
        Assert.Equal(3, result.TransactionCount);
        Assert.Equal(300m, result.TotalHistoricalSpend);
        // AverageDailySpend = 300 / 30 = 10 (historical window); the projection is about the month.
        Assert.Equal(10m, result.AverageDailySpend);
        // April has 200 spent in 15 days (13.33/day) and 15 days left: 200 + 200.
        Assert.Equal(200m, result.MonthSpentToDate);
        Assert.Equal(400m, result.ProjectedSpend);
        Assert.Equal(FixedNow, result.GeneratedAtUtc);
        Assert.Contains("3 transações", result.Assumptions);
        Assert.Contains("30 dias", result.Assumptions);
    }

    [Fact]
    public async Task Horizon90_WithTransactions_ReturnsCorrectAverageDailySpend()
    {
        var repo = new FakeCashFlowRepository();
        var coupleId = Guid.NewGuid();

        // 3 transactions totalling 900
        repo.AddTransaction(coupleId, 300m, "Moradia", FixedNow.AddDays(-15));
        repo.AddTransaction(coupleId, 300m, "Moradia", FixedNow.AddDays(-45));
        repo.AddTransaction(coupleId, 300m, "Moradia", FixedNow.AddDays(-80));

        var handler = BuildHandler(repo);
        var result = await handler.HandleAsync(new GetCashFlowQuery(coupleId, 90), CancellationToken.None);

        Assert.Equal(90, result.Horizon);
        Assert.Equal(3, result.TransactionCount);
        Assert.Equal(900m, result.TotalHistoricalSpend);
        // AverageDailySpend = 900 / 90 = 10
        Assert.Equal(10m, result.AverageDailySpend);
        Assert.Equal(0m, result.MonthSpentToDate);
        Assert.Contains("3 transações", result.Assumptions);
        Assert.Contains("90 dias", result.Assumptions);
    }

    [Fact]
    public async Task ZeroTransactions_ReturnsAllZeroedProjectionValues()
    {
        var repo = new FakeCashFlowRepository();
        var coupleId = Guid.NewGuid();

        var handler = BuildHandler(repo);
        var result = await handler.HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal(0, result.TransactionCount);
        Assert.Equal(0m, result.TotalHistoricalSpend);
        Assert.Equal(0m, result.AverageDailySpend);
        Assert.Equal(0m, result.ProjectedSpend);
        Assert.Empty(result.CategoryBreakdown);
        Assert.Contains("baseado em", result.Assumptions);
        Assert.Contains("0 transações", result.Assumptions);
        Assert.Contains("30 dias", result.Assumptions);
    }

    [Fact]
    public async Task InvalidHorizon_ThrowsBadRequestWithStableCode()
    {
        var repo = new FakeCashFlowRepository();
        var handler = BuildHandler(repo);

        var ex = await Assert.ThrowsAsync<CoupleSync.Application.Common.Exceptions.BadRequestException>(
            () => handler.HandleAsync(new GetCashFlowQuery(Guid.NewGuid(), 45), CancellationToken.None));
        Assert.Equal("INVALID_HORIZON", ex.Code);
    }

    private static IncomeSource Income(Guid coupleId, string month, string name, decimal amount, bool recurring, string currency = "BRL", Guid? userId = null)
        => IncomeSource.Create(coupleId, userId ?? Guid.NewGuid(), month, name, amount, currency, false, FixedNow, recurring);

    [Fact]
    public async Task Projection_IsIncomeMinusSpentMinusForecastOfRemainingDays()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeCashFlowRepository();
        // Oct 11 (Brasília): 11 elapsed days, 20 remaining; 330 spent => 30/day.
        var now = new DateTime(2026, 10, 11, 15, 0, 0, DateTimeKind.Utc);
        repo.AddTransaction(coupleId, 330m, "Alimentacao", new DateTime(2026, 10, 3, 15, 0, 0, DateTimeKind.Utc));
        repo.AddTransaction(coupleId, 5000m, "Moradia", new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc));

        var incomes = new FakeIncomeSourceRepository();
        incomes.Sources.Add(Income(coupleId, "2026-08", "Salário A", 5000m, recurring: true));
        incomes.Sources.Add(Income(coupleId, "2026-09", "Salário B", 1000m, recurring: true));
        incomes.Sources.Add(Income(coupleId, "2026-09", "Bônus", 700m, recurring: false));   // only September
        incomes.Sources.Add(Income(coupleId, "2026-10", "Extra", 200m, recurring: false));
        incomes.Sources.Add(Income(coupleId, "2026-08", "Dólar", 999m, recurring: true, currency: "USD")); // never in reais
        incomes.Sources.Add(Income(Guid.NewGuid(), "2026-08", "Outro grupo", 9999m, recurring: true));

        var result = await BuildHandler(repo, incomes, now).HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal("2026-10", result.Month);
        Assert.Equal(6200m, result.MonthIncome);
        Assert.Equal(330m, result.MonthSpentToDate);
        Assert.Equal(30m, result.ForecastDailyAverage);
        Assert.Equal(20, result.RemainingDays);
        Assert.Equal(600m, result.ForecastRemainingSpend);
        Assert.Equal(6200m - 330m - 600m, result.ProjectedMonthEndBalance);
        Assert.Equal(930m, result.ProjectedSpend);
    }

    [Fact]
    public async Task Projection_WithLessThanThreeDays_UsesPreviousMonthAverage()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeCashFlowRepository();
        // Oct 2 (Brasília): 2 elapsed days. September (30 days) spent 3000 => 100/day.
        var now = new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc);
        repo.AddTransaction(coupleId, 3000m, "Moradia", new DateTime(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc));
        repo.AddTransaction(coupleId, 50m, "Alimentacao", new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc));

        var result = await BuildHandler(repo, now: now).HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal(100m, result.ForecastDailyAverage);
        Assert.Equal(29, result.RemainingDays);
        Assert.Equal(2900m, result.ForecastRemainingSpend);
        Assert.Equal(-50m - 2900m, result.ProjectedMonthEndBalance);
    }

    [Fact]
    public async Task Projection_WithNoDataAtAll_ForecastsZero()
    {
        var coupleId = Guid.NewGuid();
        var incomes = new FakeIncomeSourceRepository();
        incomes.Sources.Add(Income(coupleId, "2026-10", "Salário", 4000m, recurring: true));
        var now = new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc);

        var result = await BuildHandler(new FakeCashFlowRepository(), incomes, now)
            .HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal(0m, result.ForecastDailyAverage);
        Assert.Equal(0m, result.ForecastRemainingSpend);
        Assert.Equal(4000m, result.ProjectedMonthEndBalance);
    }

    [Fact]
    public async Task Month_FollowsBrasiliaCalendar_NotUtc()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeCashFlowRepository();
        // Oct 31 23:00 in Brasília is already Nov 1 02:00 in UTC.
        var now = new DateTime(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc);
        // 22:30 of Oct 31 in Brasília (= Nov 1 01:30 UTC) belongs to October.
        repo.AddTransaction(coupleId, 300m, "Alimentacao", new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Utc));
        // 23:00 of Sep 30 in Brasília (= Oct 1 02:00 UTC) belongs to September.
        repo.AddTransaction(coupleId, 40m, "Alimentacao", new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc));

        var result = await BuildHandler(repo, now: now).HandleAsync(new GetCashFlowQuery(coupleId, 30), CancellationToken.None);

        Assert.Equal("2026-10", result.Month);
        Assert.Equal(300m, result.MonthSpentToDate);
        Assert.Equal(0, result.RemainingDays);
    }
}

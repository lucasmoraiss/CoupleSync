using CoupleSync.Application.CashFlow.Queries;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.CashFlow;

public sealed class GetCashFlowQueryHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private static GetCashFlowQueryHandler BuildHandler(FakeCashFlowRepository repo)
        => new(repo, new FixedDateTimeProvider(FixedNow));

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
        // AverageDailySpend = 300 / 30 = 10; ProjectedSpend = 10 * 30 = 300
        Assert.Equal(10m, result.AverageDailySpend);
        Assert.Equal(300m, result.ProjectedSpend);
        Assert.Equal(FixedNow, result.GeneratedAtUtc);
        Assert.Contains("3 transactions", result.Assumptions);
        Assert.Contains("30 days", result.Assumptions);
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
        Assert.Equal(900m, result.ProjectedSpend);
        Assert.Contains("90 days", result.Assumptions);
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
        Assert.Contains("0 transactions", result.Assumptions);
    }

    [Fact]
    public async Task InvalidHorizon_ThrowsArgumentException()
    {
        var repo = new FakeCashFlowRepository();
        var handler = BuildHandler(repo);

        await Assert.ThrowsAsync<ArgumentException>(
            () => handler.HandleAsync(new GetCashFlowQuery(Guid.NewGuid(), 45), CancellationToken.None));
    }
}

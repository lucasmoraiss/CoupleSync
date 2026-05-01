using CoupleSync.Application.Dashboard;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Dashboard;

public sealed class GetDashboardQueryHandlerTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc);

    private static GetDashboardQueryHandler BuildHandler(FakeDashboardRepository? repo = null)
        => new(repo ?? new FakeDashboardRepository(), new FixedDateTimeProvider(FixedNow));

    private static Transaction BuildTransaction(
        Guid coupleId,
        Guid userId,
        decimal amount = 100m,
        string category = "Alimentação",
        DateTime? eventTimestamp = null)
    {
        return Transaction.Create(
            coupleId,
            userId,
            $"fp-{Guid.NewGuid():N}",
            "NUBANK",
            amount,
            "BRL",
            eventTimestamp ?? new DateTime(2026, 4, 10, 10, 0, 0, DateTimeKind.Utc),
            "Test purchase",
            "Test Store",
            category,
            Guid.NewGuid(),
            DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_WithNoTransactions_ReturnsZeroAggregates()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(0, result.TotalExpenses);
        Assert.Equal(0, result.TransactionCount);
        Assert.Empty(result.ExpensesByCategory);
        Assert.Empty(result.PartnerBreakdown);
    }

    [Fact]
    public async Task HandleAsync_DefaultPeriod_UsesCurrentMonth()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), result.PeriodStart);
        Assert.Equal(new DateTime(2026, 4, 30, 23, 59, 59, DateTimeKind.Utc), result.PeriodEnd);
    }

    [Fact]
    public async Task HandleAsync_WithExplicitDateRange_UsesThatRange()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, start, end),
            CancellationToken.None);

        Assert.Equal(start, result.PeriodStart);
        Assert.Equal(end, result.PeriodEnd);
    }

    [Fact]
    public async Task HandleAsync_WithTransactions_ComputesTotalExpenses()
    {
        var repo = new FakeDashboardRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        repo.Transactions.Add(BuildTransaction(coupleId, userId, 150m));
        repo.Transactions.Add(BuildTransaction(coupleId, userId, 50m));

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(200m, result.TotalExpenses);
        Assert.Equal(2, result.TransactionCount);
    }

    [Fact]
    public async Task HandleAsync_WithTransactions_ComputesExpensesByCategory()
    {
        var repo = new FakeDashboardRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        repo.Transactions.Add(BuildTransaction(coupleId, userId, 100m, "Alimentação"));
        repo.Transactions.Add(BuildTransaction(coupleId, userId, 60m, "Alimentação"));
        repo.Transactions.Add(BuildTransaction(coupleId, userId, 40m, "Transporte"));

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(160m, result.ExpensesByCategory["Alimentação"]);
        Assert.Equal(40m, result.ExpensesByCategory["Transporte"]);
    }

    [Fact]
    public async Task HandleAsync_WithTwoPartners_ComputesPartnerBreakdown()
    {
        var repo = new FakeDashboardRepository();
        var coupleId = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        repo.Transactions.Add(BuildTransaction(coupleId, userA, 80m));
        repo.Transactions.Add(BuildTransaction(coupleId, userA, 20m));
        repo.Transactions.Add(BuildTransaction(coupleId, userB, 50m));

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(2, result.PartnerBreakdown.Count);
        Assert.Equal(100m, result.PartnerBreakdown.First(p => p.UserId == userA).TotalAmount);
        Assert.Equal(50m, result.PartnerBreakdown.First(p => p.UserId == userB).TotalAmount);
    }

    [Fact]
    public async Task HandleAsync_DoesNotReturnOtherCouplesTransactions()
    {
        var repo = new FakeDashboardRepository();
        var coupleId = Guid.NewGuid();
        var otherCoupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        repo.Transactions.Add(BuildTransaction(coupleId, userId, 100m));
        repo.Transactions.Add(BuildTransaction(otherCoupleId, userId, 999m));

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(100m, result.TotalExpenses);
        Assert.Equal(1, result.TransactionCount);
    }

    [Fact]
    public async Task HandleAsync_DateRangeFilter_ExcludesOutOfRangeTransactions()
    {
        var repo = new FakeDashboardRepository();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 23, 59, 59, DateTimeKind.Utc);

        repo.Transactions.Add(BuildTransaction(coupleId, userId, 100m, eventTimestamp: new DateTime(2026, 4, 15, 10, 0, 0, DateTimeKind.Utc)));
        repo.Transactions.Add(BuildTransaction(coupleId, userId, 200m, eventTimestamp: new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc)));

        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, start, end),
            CancellationToken.None);

        Assert.Equal(100m, result.TotalExpenses);
        Assert.Equal(1, result.TransactionCount);
    }

    [Fact]
    public async Task HandleAsync_GeneratedAtUtc_IsPopulated()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, null),
            CancellationToken.None);

        Assert.Equal(FixedNow, result.GeneratedAtUtc);
    }

    [Fact]
    public async Task GetDashboardQueryHandler_EndDate_MidnightIsSnappedToEndOfDay()
    {
        // A date-only endDate arrives as midnight UTC; handler must snap to 23:59:59.999.
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();
        var endDateMidnight = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, endDateMidnight),
            CancellationToken.None);

        var expectedEnd = new DateTime(2026, 4, 30, 23, 59, 59, 999, DateTimeKind.Utc);
        Assert.Equal(expectedEnd, result.PeriodEnd);
    }

    [Fact]
    public async Task GetDashboardQueryHandler_EndDate_ExplicitTimeIsNotOverridden()
    {
        // When endDate already has a non-zero time component, the handler must NOT override it.
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();
        var endDateExplicit = new DateTime(2026, 4, 30, 15, 30, 0, DateTimeKind.Utc);

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, null, endDateExplicit),
            CancellationToken.None);

        Assert.Equal(endDateExplicit, result.PeriodEnd);
    }

    [Fact]
    public async Task HandleAsync_WhenStartDateAfterEndDate_ThrowsArgumentException()
    {
        var handler = BuildHandler();
        var coupleId = Guid.NewGuid();
        var start = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                new GetDashboardQuery(coupleId, start, end),
                CancellationToken.None));

        Assert.Contains("INVALID_DATE_RANGE", ex.Message);
    }
}

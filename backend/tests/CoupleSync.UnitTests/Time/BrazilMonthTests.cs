using System.Globalization;
using CoupleSync.Application.Budget;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Dashboard;
using CoupleSync.Application.Income;
using CoupleSync.Application.Notification;
using CoupleSync.Application.Reports;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Time;

/// <summary>DAD-04/DAD-17/S3 3.82: the month is a Brasília month; income is real and recurring income carries forward.</summary>
[Trait("Category", "BrazilMonth")]
public sealed class BrazilMonthTests
{
    // 2026-10-31 23:00 in Brasília (UTC-3) is already 2026-11-01 02:00 in UTC.
    private static readonly DateTime LastNightOfOctober = new(2026, 11, 1, 2, 0, 0, DateTimeKind.Utc);
    // 2026-10-31 22:30 in Brasília.
    private static readonly DateTime LateOctoberTx = new(2026, 11, 1, 1, 30, 0, DateTimeKind.Utc);

    private static Transaction Tx(Guid coupleId, decimal amount, DateTime whenUtc, string category = "OUTROS")
        => Transaction.Create(coupleId, Guid.NewGuid(), $"fp-{Guid.NewGuid():N}", "NUBANK", amount, "BRL",
            whenUtc, "d", "m", category, Guid.NewGuid(), whenUtc);

    // ── BrazilTime ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-11-01T01:30:00Z", "2026-10")]   // 22:30 of Oct 31 in Brasília
    [InlineData("2026-11-01T02:59:59Z", "2026-10")]
    [InlineData("2026-11-01T03:00:00Z", "2026-11")]   // midnight of Nov 1 in Brasília
    [InlineData("2026-01-01T02:00:00Z", "2025-12")]   // year boundary
    public void MonthOf_UsesBrasiliaCalendar(string utc, string expected)
        => Assert.Equal(expected, BrazilTime.MonthOf(DateTime.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)));

    [Fact]
    public void MonthRangeUtc_StartsAtBrasiliaMidnightAndEndsAtTheNextOne()
    {
        var (start, end) = BrazilTime.MonthRangeUtc("2026-10");

        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc), end);
        Assert.Equal(DateTimeKind.Utc, start.Kind);
    }

    [Fact]
    public void MonthRangeUtc_DecemberEndsInJanuary()
    {
        var (_, end) = BrazilTime.MonthRangeUtc("2026-12");
        Assert.Equal(new DateTime(2027, 1, 1, 3, 0, 0, DateTimeKind.Utc), end);
    }

    [Theory]
    [InlineData("2026-01", -1, "2025-12")]
    [InlineData("2026-10", -11, "2025-11")]
    [InlineData("2026-12", 1, "2027-01")]
    public void AddMonths_CrossesYears(string month, int delta, string expected)
        => Assert.Equal(expected, BrazilTime.AddMonths(month, delta));

    // ── Dashboard ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_DefaultPeriod_IsTheBrasiliaMonth()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeDashboardRepository();
        repo.Transactions.Add(Tx(coupleId, 100m, LateOctoberTx));                                           // October (Brasília)
        repo.Transactions.Add(Tx(coupleId, 40m, new DateTime(2026, 10, 1, 2, 0, 0, DateTimeKind.Utc)));    // Sep 30 23:00 Brasília
        var handler = new GetDashboardQueryHandler(repo, new FixedDateTimeProvider(LastNightOfOctober));

        var result = await handler.HandleAsync(new GetDashboardQuery(coupleId, null, null), CancellationToken.None);

        Assert.Equal(100m, result.TotalExpenses);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), result.PeriodStart);
        Assert.Equal(new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc).AddMilliseconds(-1), result.PeriodEnd);
    }

    [Fact]
    public async Task Dashboard_DateOnlyRange_IsReadAsBrasiliaDays()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeDashboardRepository();
        repo.Transactions.Add(Tx(coupleId, 100m, LateOctoberTx));
        var handler = new GetDashboardQueryHandler(repo, new FixedDateTimeProvider(LastNightOfOctober));

        var result = await handler.HandleAsync(
            new GetDashboardQuery(coupleId, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)), CancellationToken.None);

        Assert.Equal(100m, result.TotalExpenses);
        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), result.PeriodStart);
    }

    // ── Budget ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Budget_CurrentPlan_IsTheBrasiliaMonthAndCountsItsLateNightSpend()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        var plan = BudgetPlan.Create(coupleId, "2026-10", 5000m, "BRL", LastNightOfOctober);
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "OUTROS", 1000m, "BRL", LastNightOfOctober));
        repo.Plans.Add(plan);
        txRepo.Transactions.Add(Tx(coupleId, 300m, LateOctoberTx));
        txRepo.Transactions.Add(Tx(coupleId, 70m, new DateTime(2026, 11, 1, 3, 30, 0, DateTimeKind.Utc))); // November
        var service = new BudgetService(repo, txRepo, new FixedDateTimeProvider(LastNightOfOctober));

        var current = await service.GetCurrentPlanAsync(coupleId, CancellationToken.None);

        Assert.NotNull(current);
        Assert.Equal("2026-10", current!.Month);
        Assert.Equal(300m, current.Allocations.Single().ActualSpent);
    }

    // ── Budget alerts ──────────────────────────────────────────────────────

    [Fact]
    public async Task BudgetAlert_LateNightTransaction_CountsInTheBrasiliaMonth()
    {
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var budgets = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        var plan = BudgetPlan.Create(coupleId, "2026-10", 5000m, "BRL", LastNightOfOctober);
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "OUTROS", 100m, "BRL", LastNightOfOctober));
        budgets.Plans.Add(plan);
        var tx = Tx(coupleId, 150m, LateOctoberTx);
        txRepo.Transactions.Add(tx);
        var settings = NotificationSettings.Create(userId, coupleId, LastNightOfOctober);
        var svc = new AlertPolicyService(budgets, txRepo, new FakeNotificationEventRepository());

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, [], settings, LastNightOfOctober);

        Assert.Contains(events, e => e.AlertType == "BudgetExceeded|OUTROS|2026-10");
    }

    // ── Recurring income ───────────────────────────────────────────────────

    private static IncomeSource Source(Guid coupleId, Guid userId, string month, string name, decimal amount, bool recurring, bool shared = false)
        => IncomeSource.Create(coupleId, userId, month, name, amount, "BRL", shared, LastNightOfOctober, recurring);

    [Fact]
    public void Recurring_CountsFromItsStartMonthOnward_AndNonRecurringOnlyInItsOwnMonth()
    {
        var coupleId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var all = new[]
        {
            Source(coupleId, a, "2026-08", "Salário", 5000m, recurring: true),
            Source(coupleId, a, "2026-09", "Bônus", 700m, recurring: false),
        };

        Assert.Empty(IncomeSchedule.EffectiveIn(all, "2026-07"));
        Assert.Equal(5000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-08")));
        Assert.Equal(5700m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-09")));
        Assert.Equal(5000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-10")));
        Assert.Equal(5000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2027-03")));
    }

    [Fact]
    public void Recurring_NewerRegistrationOfTheSameNameReplacesItFromItsMonth_LeavingThePastAlone()
    {
        var coupleId = Guid.NewGuid();
        var a = Guid.NewGuid();
        var all = new[]
        {
            Source(coupleId, a, "2026-01", "Salário", 4000m, recurring: true),
            Source(coupleId, a, "2026-06", "salário", 5000m, recurring: true),
        };

        Assert.Equal(4000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-05")));
        Assert.Equal(5000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-06")));
        Assert.Equal(5000m, IncomeSchedule.TotalInReais(IncomeSchedule.EffectiveIn(all, "2026-10")));
    }

    [Fact]
    public async Task IncomeService_MonthlyIncome_CarriesRecurringForwardAcrossMembers()
    {
        var repo = new FakeIncomeSourceRepository();
        var coupleRepo = new FakeCoupleRepository();
        var couple = Couple.Create("ABC123", LastNightOfOctober);
        var u1 = User.Create(EmailAddress.From("a@test.com"), "Ana", "h", LastNightOfOctober);
        var u2 = User.Create(EmailAddress.From("b@test.com"), "Bia", "h", LastNightOfOctober);
        couple.AddMember(u1, LastNightOfOctober);
        couple.AddMember(u2, LastNightOfOctober);
        coupleRepo.Couples.Add(couple);
        repo.Sources.Add(Source(couple.Id, u1.Id, "2026-08", "Salário", 5000m, recurring: true));
        repo.Sources.Add(Source(couple.Id, u2.Id, "2026-09", "Salário", 3000m, recurring: true));
        repo.Sources.Add(Source(couple.Id, u2.Id, "2026-09", "Freela", 500m, recurring: false));
        var service = new IncomeService(repo, coupleRepo, new FixedDateTimeProvider(LastNightOfOctober));

        var october = await service.GetCurrentMonthIncomeAsync(couple.Id, u1.Id, CancellationToken.None);
        var august = await service.GetMonthlyIncomeAsync(couple.Id, u1.Id, "2026-08", CancellationToken.None);

        Assert.Equal("2026-10", october.Month);
        Assert.Equal(8000m, october.CoupleTotal);
        Assert.Equal(5000m, august.CoupleTotal);
    }

    // ── Reports ────────────────────────────────────────────────────────────

    private sealed class FakeReportsRepository : IReportsRepository
    {
        public List<MonthlySpendingRow> Monthly { get; } = new();
        public DateTime? LastFrom { get; private set; }

        public Task<IReadOnlyList<CategorySpendingRow>> GetSpendingByCategoryAsync(Guid coupleId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            LastFrom = fromUtc;
            return Task.FromResult<IReadOnlyList<CategorySpendingRow>>([]);
        }

        public Task<IReadOnlyList<MonthlySpendingRow>> GetMonthlySpendingAsync(Guid coupleId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            LastFrom = fromUtc;
            return Task.FromResult<IReadOnlyList<MonthlySpendingRow>>(Monthly);
        }
    }

    [Fact]
    public async Task Reports_MonthlyTrends_ReturnRealIncomeAndBalance()
    {
        var coupleId = Guid.NewGuid();
        var reports = new FakeReportsRepository();
        reports.Monthly.Add(new MonthlySpendingRow(2026, 9, 1200m));
        reports.Monthly.Add(new MonthlySpendingRow(2026, 10, 800m));
        var incomes = new FakeIncomeSourceRepository();
        incomes.Sources.Add(Source(coupleId, Guid.NewGuid(), "2026-09", "Salário", 5000m, recurring: true));
        incomes.Sources.Add(Source(coupleId, Guid.NewGuid(), "2026-10", "Extra", 300m, recurring: false, shared: true));
        var service = new ReportsService(reports, incomes, new FixedDateTimeProvider(LastNightOfOctober));

        var result = await service.GetMonthlyTrendsAsync(coupleId, 3, CancellationToken.None);

        Assert.Equal(["2026-08", "2026-09", "2026-10"], result.Months.Select(m => m.Month).ToArray());
        Assert.Equal([0m, 5000m, 5300m], result.Months.Select(m => m.Income).ToArray());
        Assert.Equal([0m, 1200m, 800m], result.Months.Select(m => m.Expense).ToArray());
        Assert.Equal([0m, 3800m, 4500m], result.Months.Select(m => m.Net).ToArray());
        // The window starts at the Brasília midnight of Aug 1, not at UTC midnight.
        Assert.Equal(new DateTime(2026, 8, 1, 3, 0, 0, DateTimeKind.Utc), reports.LastFrom);
    }
}

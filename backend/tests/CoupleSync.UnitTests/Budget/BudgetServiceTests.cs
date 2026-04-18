using CoupleSync.Application.Budget;
using CoupleSync.Application.Budget.Commands;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Budget;

[Trait("Category", "Budget")]
public sealed class BudgetServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc);
    private const string FixedMonth = "2026-04";

    private static (BudgetService Service, FakeBudgetRepository Repo, FakeTransactionRepository TxRepo) Build()
    {
        var repo = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        var dt = new FixedDateTimeProvider(FixedNow);
        var service = new BudgetService(repo, txRepo, dt);
        return (service, repo, txRepo);
    }

    private static BudgetPlan SeedPlan(FakeBudgetRepository repo, Guid coupleId, string month = FixedMonth)
    {
        var plan = BudgetPlan.Create(coupleId, month, 5000m, "BRL", FixedNow);
        repo.Plans.Add(plan);
        return plan;
    }

    // ── UpsertPlanAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task UpsertPlanAsync_NewPlan_CreatesPlanAndReturnsDto()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();

        var result = await service.UpsertPlanAsync(coupleId, FixedMonth, 5000m, "BRL", CancellationToken.None);

        Assert.Equal(coupleId, result.CoupleId);
        Assert.Equal(FixedMonth, result.Month);
        Assert.Equal(5000m, result.GrossIncome);
        Assert.Equal("BRL", result.Currency);
        Assert.Single(repo.Plans);
    }

    [Fact]
    public async Task UpsertPlanAsync_ExistingPlan_UpdatesAndReturnsDto()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        SeedPlan(repo, coupleId);

        var result = await service.UpsertPlanAsync(coupleId, FixedMonth, 8000m, "BRL", CancellationToken.None);

        Assert.Equal(8000m, result.GrossIncome);
        Assert.Single(repo.Plans); // still one plan, updated in place
    }

    // ── ReplaceAllocationsAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReplaceAllocationsAsync_ValidAllocations_ReplacesAndReturns()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var allocations = new List<AllocationInput>
        {
            new("Food", 1000m, "BRL"),
            new("Transport", 500m, "BRL")
        };

        var result = await service.ReplaceAllocationsAsync(coupleId, plan.Id, allocations, CancellationToken.None);

        Assert.Equal(2, result.Allocations.Count);
        Assert.Contains(result.Allocations, a => a.Category == "Food" && a.AllocatedAmount == 1000m);
        Assert.Contains(result.Allocations, a => a.Category == "Transport" && a.AllocatedAmount == 500m);
    }

    [Fact]
    public async Task ReplaceAllocationsAsync_MoreThan20_ThrowsUnprocessableEntity()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var allocations = Enumerable.Range(1, 21)
            .Select(i => new AllocationInput($"Category{i}", 100m, "BRL"))
            .ToList();

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(() =>
            service.ReplaceAllocationsAsync(coupleId, plan.Id, allocations, CancellationToken.None));

        Assert.Equal("BUDGET_ALLOCATION_LIMIT", ex.Code);
    }

    [Fact]
    public async Task ReplaceAllocationsAsync_DuplicateCategory_ThrowsUnprocessableEntity()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var allocations = new List<AllocationInput>
        {
            new("Food", 1000m, "BRL"),
            new("Food", 500m, "BRL") // duplicate
        };

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(() =>
            service.ReplaceAllocationsAsync(coupleId, plan.Id, allocations, CancellationToken.None));

        Assert.Equal("BUDGET_ALLOCATION_DUPLICATE_CATEGORY", ex.Code);
    }

    [Fact]
    public async Task ReplaceAllocationsAsync_CurrencyMismatch_ThrowsUnprocessableEntity()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId); // plan currency = BRL

        var allocations = new List<AllocationInput>
        {
            new("Food", 1000m, "USD") // mismatch: plan is BRL, allocation is USD
        };

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(() =>
            service.ReplaceAllocationsAsync(coupleId, plan.Id, allocations, CancellationToken.None));

        Assert.Equal("BUDGET_ALLOCATION_CURRENCY_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task ReplaceAllocationsAsync_PlanNotFound_ThrowsNotFoundException()
    {
        var (service, _, _) = Build();
        var coupleId = Guid.NewGuid();
        var missingPlanId = Guid.NewGuid();

        var allocations = new List<AllocationInput>
        {
            new("Food", 500m, "BRL")
        };

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.ReplaceAllocationsAsync(coupleId, missingPlanId, allocations, CancellationToken.None));

        Assert.Equal("BUDGET_PLAN_NOT_FOUND", ex.Code);
    }

    // ── GetPlanAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPlanAsync_WithActualSpent_IncludesSpentAndRemaining()
    {
        var (service, repo, txRepo) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        // Add a "Food" allocation to the plan
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "Food", 1000m, "BRL", FixedNow));

        // Add a transaction in April 2026 for the "Food" category
        var tx = Transaction.Create(
            coupleId,
            Guid.NewGuid(),
            "fp1",
            "NUBANK",
            300m,
            "BRL",
            new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            "Groceries",
            "Supermarket",
            "Food",
            Guid.NewGuid(),
            FixedNow);
        txRepo.Transactions.Add(tx);

        var result = await service.GetPlanAsync(coupleId, FixedMonth, CancellationToken.None);

        Assert.NotNull(result);
        var foodAlloc = result!.Allocations.Single(a => a.Category == "Food");
        Assert.Equal(300m, foodAlloc.ActualSpent);
        Assert.Equal(700m, foodAlloc.Remaining);
    }

    // ── GetCurrentPlanAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentPlanAsync_ReturnsCurrentMonthPlan()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();

        // FixedNow is 2026-04-17, so current month is "2026-04"
        SeedPlan(repo, coupleId, "2026-04");

        var result = await service.GetCurrentPlanAsync(coupleId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("2026-04", result!.Month);
    }

    // ── ComputeGap ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeGap_ReturnsCorrectDifference()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);  // grossIncome = 5000

        var allocations = new List<AllocationInput>
        {
            new("Food", 1000m, "BRL"),
            new("Transport", 500m, "BRL"),
            new("Bills", 1500m, "BRL")
        };

        var dto = await service.ReplaceAllocationsAsync(coupleId, plan.Id, allocations, CancellationToken.None);
        var gap = service.ComputeGap(dto);

        // 5000 - (1000 + 500 + 1500) = 2000
        Assert.Equal(2000m, gap);
    }
}

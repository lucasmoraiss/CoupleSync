using CoupleSync.Application.Budget;
using CoupleSync.Application.Budget.Commands;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Budget;

/// <summary>Allocations take any spelling of a category but are stored and compared by canonical key.</summary>
[Trait("Category", "Budget")]
public sealed class BudgetCategoryNormalizationTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 17, 10, 0, 0, DateTimeKind.Utc);

    private static (BudgetService Service, FakeBudgetRepository Repo, FakeTransactionRepository TxRepo) Build()
    {
        var repo = new FakeBudgetRepository();
        var txRepo = new FakeTransactionRepository();
        return (new BudgetService(repo, txRepo, new FixedDateTimeProvider(FixedNow)), repo, txRepo);
    }

    private static BudgetPlan SeedPlan(FakeBudgetRepository repo, Guid coupleId)
    {
        var plan = BudgetPlan.Create(coupleId, "2026-04", 5000m, "BRL", FixedNow);
        repo.Plans.Add(plan);
        return plan;
    }

    [Fact]
    public async Task ReplaceAllocations_StoresTheCanonicalKey_WhateverTheSpelling()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var result = await service.ReplaceAllocationsAsync(coupleId, plan.Id,
            [new("Alimentação", 1000m, "BRL"), new(" saude ", 300m, "BRL"), new("OUTROS", 10m, "BRL")],
            CancellationToken.None);

        Assert.Equal(new[] { "ALIMENTACAO", "SAUDE", "OUTROS" }, result.Allocations.Select(a => a.Category));
    }

    [Fact]
    public async Task ReplaceAllocations_TwoSpellingsOfOneCategory_AreADuplicate()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(() => service.ReplaceAllocationsAsync(
            coupleId, plan.Id, [new("Alimentação", 1m, "BRL"), new("ALIMENTACAO", 2m, "BRL")], CancellationToken.None));

        Assert.Equal("BUDGET_ALLOCATION_DUPLICATE_CATEGORY", ex.Code);
    }

    [Fact]
    public async Task ReplaceAllocations_UnknownCategory_Throws400WithTheAcceptedList()
    {
        var (service, repo, _) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => service.ReplaceAllocationsAsync(
            coupleId, plan.Id, [new("Mercado", 1m, "BRL")], CancellationToken.None));

        Assert.Equal("INVALID_CATEGORY", ex.Code);
        Assert.Contains("ALIMENTACAO", ex.Message);
    }

    [Fact]
    public async Task GetPlan_SpentIsFoundForAnAllocationStoredBeforeTheNormalization()
    {
        var (service, repo, txRepo) = Build();
        var coupleId = Guid.NewGuid();
        var plan = SeedPlan(repo, coupleId);
        plan.Allocations.Add(BudgetAllocation.Create(plan.Id, "Alimentação", 1000m, "BRL", FixedNow));
        await txRepo.AddTransactionAsync(Transaction.Create(
            coupleId, Guid.NewGuid(), "fp", "NUBANK", 300m, "BRL", new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc),
            "d", "m", "ALIMENTACAO", Guid.NewGuid(), FixedNow), CancellationToken.None);

        var result = await service.GetPlanAsync(coupleId, "2026-04", CancellationToken.None);

        var allocation = Assert.Single(result!.Allocations);
        Assert.Equal(300m, allocation.ActualSpent);
        Assert.Equal(700m, allocation.Remaining);
    }
}

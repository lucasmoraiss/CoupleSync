using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Income;
using CoupleSync.Application.Income.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Income;

[Trait("Category", "Income")]
public sealed class IncomeServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);
    private const string FixedMonth = "2026-05";

    private static (IncomeService Service, FakeIncomeSourceRepository Repo, FakeCoupleRepository CoupleRepo) Build()
    {
        var repo = new FakeIncomeSourceRepository();
        var coupleRepo = new FakeCoupleRepository();
        var dt = new FixedDateTimeProvider(FixedNow);
        var service = new IncomeService(repo, coupleRepo, dt);
        return (service, repo, coupleRepo);
    }

    private static (Couple Couple, User User1, User User2) SeedCouple(FakeCoupleRepository coupleRepo)
    {
        var couple = Couple.Create("ABC123", FixedNow);
        var user1 = User.Create(EmailAddress.From("user1@test.com"), "Lucas", "hash1", FixedNow);
        var user2 = User.Create(EmailAddress.From("user2@test.com"), "Partner", "hash2", FixedNow);
        couple.AddMember(user1, FixedNow);
        couple.AddMember(user2, FixedNow);
        coupleRepo.Couples.Add(couple);
        return (couple, user1, user2);
    }

    private static IncomeSource SeedSource(
        FakeIncomeSourceRepository repo, Guid coupleId, Guid userId,
        string name = "Salário", decimal amount = 5000m, bool isShared = false, string month = FixedMonth)
    {
        var source = IncomeSource.Create(coupleId, userId, month, name, amount, "BRL", isShared, FixedNow);
        repo.Sources.Add(source);
        return source;
    }

    // ── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ValidInput_CreatesAndReturnsDto()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        var input = new CreateIncomeSourceInput("Salário", 5000m, "BRL", false);
        var result = await service.CreateAsync(couple.Id, user1.Id, FixedMonth, input, CancellationToken.None);

        Assert.Equal("Salário", result.Name);
        Assert.Equal(5000m, result.Amount);
        Assert.Equal(user1.Id, result.UserId);
        Assert.False(result.IsShared);
        Assert.Single(repo.Sources);
    }

    [Fact]
    public async Task CreateAsync_SharedSource_SetsIsSharedTrue()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        var input = new CreateIncomeSourceInput("Aluguel compartilhado", 1500m, "BRL", true);
        var result = await service.CreateAsync(couple.Id, user1.Id, FixedMonth, input, CancellationToken.None);

        Assert.True(result.IsShared);
    }

    [Fact]
    public async Task CreateAsync_ExceedsLimit_ThrowsUnprocessableEntity()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        for (int i = 0; i < 20; i++)
            SeedSource(repo, couple.Id, user1.Id, $"Source{i}");

        var input = new CreateIncomeSourceInput("OneMore", 100m, "BRL", false);
        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(() =>
            service.CreateAsync(couple.Id, user1.Id, FixedMonth, input, CancellationToken.None));

        Assert.Equal("INCOME_SOURCE_LIMIT", ex.Code);
    }

    // ── UpdateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_OwnerUpdatesName_Succeeds()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id);

        var input = new UpdateIncomeSourceInput("Novo Salário", null, null);
        var result = await service.UpdateAsync(couple.Id, user1.Id, source.Id, input, CancellationToken.None);

        Assert.Equal("Novo Salário", result.Name);
    }

    [Fact]
    public async Task UpdateAsync_OwnerUpdatesAmount_Succeeds()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id);

        var input = new UpdateIncomeSourceInput(null, 8000m, null);
        var result = await service.UpdateAsync(couple.Id, user1.Id, source.Id, input, CancellationToken.None);

        Assert.Equal(8000m, result.Amount);
    }

    [Fact]
    public async Task UpdateAsync_NonOwnerNonShared_ThrowsForbidden()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id, isShared: false);

        var input = new UpdateIncomeSourceInput("Hack", null, null);
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.UpdateAsync(couple.Id, user2.Id, source.Id, input, CancellationToken.None));

        Assert.Equal("INCOME_SOURCE_FORBIDDEN", ex.Code);
    }

    [Fact]
    public async Task UpdateAsync_NonOwnerSharedSource_Succeeds()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id, name: "Shared", isShared: true);

        var input = new UpdateIncomeSourceInput(null, 2000m, null);
        var result = await service.UpdateAsync(couple.Id, user2.Id, source.Id, input, CancellationToken.None);

        Assert.Equal(2000m, result.Amount);
    }

    [Fact]
    public async Task UpdateAsync_NotFound_ThrowsNotFoundException()
    {
        var (service, _, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        var input = new UpdateIncomeSourceInput("X", null, null);
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.UpdateAsync(couple.Id, user1.Id, Guid.NewGuid(), input, CancellationToken.None));

        Assert.Equal("INCOME_SOURCE_NOT_FOUND", ex.Code);
    }

    // ── DeleteAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_Owner_Deletes()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id);

        await service.DeleteAsync(couple.Id, user1.Id, source.Id, CancellationToken.None);

        Assert.Empty(repo.Sources);
    }

    [Fact]
    public async Task DeleteAsync_NonOwnerNonShared_ThrowsForbidden()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);
        var source = SeedSource(repo, couple.Id, user1.Id, isShared: false);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.DeleteAsync(couple.Id, user2.Id, source.Id, CancellationToken.None));

        Assert.Equal("INCOME_SOURCE_FORBIDDEN", ex.Code);
        Assert.Single(repo.Sources); // not deleted
    }

    [Fact]
    public async Task DeleteAsync_NonOwnerSharedSource_Succeeds()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);
        SeedSource(repo, couple.Id, user1.Id, name: "Shared", isShared: true);

        var source = repo.Sources.Single();
        await service.DeleteAsync(couple.Id, user2.Id, source.Id, CancellationToken.None);

        Assert.Empty(repo.Sources);
    }

    [Fact]
    public async Task DeleteAsync_NotFound_ThrowsNotFoundException()
    {
        var (service, _, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.DeleteAsync(couple.Id, user1.Id, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("INCOME_SOURCE_NOT_FOUND", ex.Code);
    }

    // ── GetMonthlyIncomeAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetMonthlyIncomeAsync_MixedSources_GroupsCorrectly()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);

        SeedSource(repo, couple.Id, user1.Id, "Salário", 5000m, false);
        SeedSource(repo, couple.Id, user1.Id, "Freelance", 2000m, false);
        SeedSource(repo, couple.Id, user2.Id, "Salário", 4000m, false);
        SeedSource(repo, couple.Id, user1.Id, "Aluguel", 1500m, true);

        var result = await service.GetMonthlyIncomeAsync(couple.Id, user1.Id, FixedMonth, CancellationToken.None);

        Assert.Equal(FixedMonth, result.Month);
        Assert.Equal("BRL", result.Currency);

        // Personal income (user1, non-shared)
        Assert.Equal(2, result.PersonalIncome.Sources.Count);
        Assert.Equal(7000m, result.PersonalIncome.Total);
        Assert.Equal("Lucas", result.PersonalIncome.UserName);

        // Partner income (user2, non-shared)
        Assert.NotNull(result.PartnerIncome);
        Assert.Single(result.PartnerIncome!.Sources);
        Assert.Equal(4000m, result.PartnerIncome.Total);
        Assert.Equal("Partner", result.PartnerIncome.UserName);

        // Shared income
        Assert.Single(result.SharedIncome.Sources);
        Assert.Equal(1500m, result.SharedIncome.Total);

        // Total
        Assert.Equal(12500m, result.CoupleTotal);
    }

    [Fact]
    public async Task GetMonthlyIncomeAsync_EmptyMonth_ReturnsZeroTotals()
    {
        var (service, _, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        var result = await service.GetMonthlyIncomeAsync(couple.Id, user1.Id, FixedMonth, CancellationToken.None);

        Assert.Equal(0m, result.PersonalIncome.Total);
        Assert.Equal(0m, result.PartnerIncome?.Total ?? 0m);
        Assert.Equal(0m, result.SharedIncome.Total);
        Assert.Equal(0m, result.CoupleTotal);
    }

    [Fact]
    public async Task GetMonthlyIncomeAsync_PartnerSourcesAreReadOnly_OnlyOwnerAndSharedEditable()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, user2) = SeedCouple(coupleRepo);

        var partnerSource = SeedSource(repo, couple.Id, user2.Id, "Salário do Parceiro", 4000m, false);

        // User1 should NOT be able to edit partner's non-shared source
        Assert.False(partnerSource.CanBeEditedBy(user1.Id));
        Assert.True(partnerSource.CanBeEditedBy(user2.Id));
    }

    [Fact]
    public async Task GetMonthlyIncomeAsync_OnlyOneMember_NoPartnerGroup()
    {
        var (service, repo, coupleRepo) = Build();
        var couple = Couple.Create("XYZ789", FixedNow);
        var user1 = User.Create(EmailAddress.From("solo@test.com"), "Solo", "hash", FixedNow);
        couple.AddMember(user1, FixedNow);
        coupleRepo.Couples.Add(couple);

        SeedSource(repo, couple.Id, user1.Id, "Salário", 5000m);

        var result = await service.GetMonthlyIncomeAsync(couple.Id, user1.Id, FixedMonth, CancellationToken.None);

        Assert.Null(result.PartnerIncome);
        Assert.Equal(5000m, result.PersonalIncome.Total);
        Assert.Equal(5000m, result.CoupleTotal);
    }

    // ── GetCurrentMonthIncomeAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetCurrentMonthIncomeAsync_UsesCurrentMonth()
    {
        var (service, repo, coupleRepo) = Build();
        var (couple, user1, _) = SeedCouple(coupleRepo);

        SeedSource(repo, couple.Id, user1.Id, "Salário", 5000m, false, FixedMonth);

        var result = await service.GetCurrentMonthIncomeAsync(couple.Id, user1.Id, CancellationToken.None);

        Assert.Equal(FixedMonth, result.Month);
        Assert.Equal(5000m, result.PersonalIncome.Total);
    }
}

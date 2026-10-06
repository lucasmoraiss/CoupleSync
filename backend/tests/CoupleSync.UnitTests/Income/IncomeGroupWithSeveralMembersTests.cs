using CoupleSync.Application.Income;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Income;

/// <summary>
/// A13 — the API allows groups with three or more members. The monthly income summary must add
/// up the income of every member and show all the other members to whoever is asking.
/// </summary>
[Trait("Category", "Income")]
public sealed class IncomeGroupWithSeveralMembersTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);
    private const string Month = "2026-05";

    private sealed record Scenario(IncomeService Service, Couple Couple, User A, User B, User D);

    /// <summary>A earns 5000, B earns 3000, D earns 2000 and there is one shared income of 1000.</summary>
    private static Scenario BuildGroupOfThree()
    {
        var sources = new FakeIncomeSourceRepository();
        var couples = new FakeCoupleRepository();
        var service = new IncomeService(sources, couples, new FixedDateTimeProvider(FixedNow));

        var couple = Couple.Create("ABC123", FixedNow);
        var a = User.Create(EmailAddress.From("a@test.com"), "Ana", "hash", FixedNow);
        var b = User.Create(EmailAddress.From("b@test.com"), "Bruno", "hash", FixedNow);
        var d = User.Create(EmailAddress.From("d@test.com"), "Dani", "hash", FixedNow);
        couple.AddMember(a, FixedNow);
        couple.AddMember(b, FixedNow);
        couple.AddMember(d, FixedNow);
        couples.Couples.Add(couple);

        sources.Sources.Add(IncomeSource.Create(couple.Id, a.Id, Month, "Salário A", 5000m, "BRL", false, FixedNow));
        sources.Sources.Add(IncomeSource.Create(couple.Id, b.Id, Month, "Salário B", 3000m, "BRL", false, FixedNow));
        sources.Sources.Add(IncomeSource.Create(couple.Id, d.Id, Month, "Salário D", 2000m, "BRL", false, FixedNow));
        sources.Sources.Add(IncomeSource.Create(couple.Id, b.Id, Month, "Aluguel", 1000m, "BRL", true, FixedNow));

        return new Scenario(service, couple, a, b, d);
    }

    [Fact]
    public async Task CoupleTotal_IsTheSameForEveryMember_AndCountsEveryone()
    {
        var s = BuildGroupOfThree();

        var forA = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.A.Id, Month, CancellationToken.None);
        var forB = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.B.Id, Month, CancellationToken.None);
        var forD = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.D.Id, Month, CancellationToken.None);

        Assert.Equal(11000m, forA.CoupleTotal);
        Assert.Equal(11000m, forB.CoupleTotal);
        Assert.Equal(11000m, forD.CoupleTotal);
    }

    [Fact]
    public async Task PartnersIncome_ListsEveryOtherMember()
    {
        var s = BuildGroupOfThree();

        var forA = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.A.Id, Month, CancellationToken.None);

        Assert.Equal(5000m, forA.PersonalIncome.Total);
        Assert.Equal(1000m, forA.SharedIncome.Total);

        Assert.Equal(2, forA.PartnersIncome.Count);
        var bruno = Assert.Single(forA.PartnersIncome, g => g.UserId == s.B.Id);
        var dani = Assert.Single(forA.PartnersIncome, g => g.UserId == s.D.Id);
        Assert.Equal("Bruno", bruno.UserName);
        Assert.Equal(3000m, bruno.Total);
        Assert.Equal("Dani", dani.UserName);
        Assert.Equal(2000m, dani.Total);

        // D (the member that used to be left out) also sees the two others.
        var forD = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.D.Id, Month, CancellationToken.None);
        Assert.Equal(2000m, forD.PersonalIncome.Total);
        Assert.Equal(new[] { s.A.Id, s.B.Id }.OrderBy(x => x), forD.PartnersIncome.Select(g => g.UserId!.Value).OrderBy(x => x));
    }

    [Fact]
    public async Task PartnerIncome_ExistingField_AggregatesAllOtherMembers_SoThePartsAddUpToTheTotal()
    {
        var s = BuildGroupOfThree();

        var forA = await s.Service.GetMonthlyIncomeAsync(s.Couple.Id, s.A.Id, Month, CancellationToken.None);

        Assert.NotNull(forA.PartnerIncome);
        Assert.Equal(5000m, forA.PartnerIncome!.Total);
        Assert.Equal(2, forA.PartnerIncome.Sources.Count);
        Assert.Contains(forA.PartnerIncome.Sources, x => x.Name == "Salário B");
        Assert.Contains(forA.PartnerIncome.Sources, x => x.Name == "Salário D");
        Assert.Equal(
            forA.CoupleTotal,
            forA.PersonalIncome.Total + forA.PartnerIncome.Total + forA.SharedIncome.Total);
    }

    [Fact]
    public async Task GroupOfTwo_KeepsTheOriginalShape()
    {
        var sources = new FakeIncomeSourceRepository();
        var couples = new FakeCoupleRepository();
        var service = new IncomeService(sources, couples, new FixedDateTimeProvider(FixedNow));

        var couple = Couple.Create("XYZ789", FixedNow);
        var a = User.Create(EmailAddress.From("a2@test.com"), "Ana", "hash", FixedNow);
        var b = User.Create(EmailAddress.From("b2@test.com"), "Bruno", "hash", FixedNow);
        couple.AddMember(a, FixedNow);
        couple.AddMember(b, FixedNow);
        couples.Couples.Add(couple);
        sources.Sources.Add(IncomeSource.Create(couple.Id, a.Id, Month, "Salário A", 5000m, "BRL", false, FixedNow));
        sources.Sources.Add(IncomeSource.Create(couple.Id, b.Id, Month, "Salário B", 3000m, "BRL", false, FixedNow));

        var forA = await service.GetMonthlyIncomeAsync(couple.Id, a.Id, Month, CancellationToken.None);

        Assert.Equal(8000m, forA.CoupleTotal);
        Assert.Equal(b.Id, forA.PartnerIncome!.UserId);
        Assert.Equal("Bruno", forA.PartnerIncome.UserName);
        Assert.Equal(3000m, forA.PartnerIncome.Total);
        var only = Assert.Single(forA.PartnersIncome);
        Assert.Equal(b.Id, only.UserId);
    }

    [Fact]
    public async Task MemberAlone_HasNoPartnerGroups()
    {
        var sources = new FakeIncomeSourceRepository();
        var couples = new FakeCoupleRepository();
        var service = new IncomeService(sources, couples, new FixedDateTimeProvider(FixedNow));

        var couple = Couple.Create("SOLO01", FixedNow);
        var a = User.Create(EmailAddress.From("solo@test.com"), "Ana", "hash", FixedNow);
        couple.AddMember(a, FixedNow);
        couples.Couples.Add(couple);
        sources.Sources.Add(IncomeSource.Create(couple.Id, a.Id, Month, "Salário A", 5000m, "BRL", false, FixedNow));

        var forA = await service.GetMonthlyIncomeAsync(couple.Id, a.Id, Month, CancellationToken.None);

        Assert.Null(forA.PartnerIncome);
        Assert.Empty(forA.PartnersIncome);
        Assert.Equal(5000m, forA.CoupleTotal);
    }
}

using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Income;

[Trait("Category", "Income")]
public sealed class IncomeSourceEntityTests
{
    private static readonly DateTime FixedNow = new(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_ValidInput_ReturnsIncomeSource()
    {
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var source = IncomeSource.Create(coupleId, userId, "2026-05", "Salário", 5000m, "BRL", false, FixedNow);

        Assert.Equal(coupleId, source.CoupleId);
        Assert.Equal(userId, source.UserId);
        Assert.Equal("2026-05", source.Month);
        Assert.Equal("Salário", source.Name);
        Assert.Equal(5000m, source.Amount);
        Assert.Equal("BRL", source.Currency);
        Assert.False(source.IsShared);
        Assert.Equal(FixedNow, source.CreatedAtUtc);
        Assert.Equal(FixedNow, source.UpdatedAtUtc);
    }

    [Fact]
    public void Create_SharedSource_IsSharedTrue()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Aluguel", 1500m, "BRL", true, FixedNow);

        Assert.True(source.IsShared);
    }

    [Fact]
    public void Create_TrimsName()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "  Freelance  ", 2000m, "BRL", false, FixedNow);

        Assert.Equal("Freelance", source.Name);
    }

    [Fact]
    public void Create_ZeroAmount_Succeeds()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Pendente", 0m, "BRL", false, FixedNow);

        Assert.Equal(0m, source.Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_InvalidName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(() =>
            IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", name!, 1000m, "BRL", false, FixedNow));
    }

    [Fact]
    public void Create_NameTooLong_Throws()
    {
        var longName = new string('A', 65);

        Assert.Throws<ArgumentException>(() =>
            IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", longName, 1000m, "BRL", false, FixedNow));
    }

    [Fact]
    public void Create_NegativeAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", -1m, "BRL", false, FixedNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("A")]
    public void Create_InvalidCurrency_Throws(string currency)
    {
        Assert.Throws<ArgumentException>(() =>
            IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", 1000m, currency, false, FixedNow));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("abcd-ef")]
    public void Create_InvalidMonth_Throws(string month)
    {
        Assert.Throws<ArgumentException>(() =>
            IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), month, "Salário", 1000m, "BRL", false, FixedNow));
    }

    [Fact]
    public void Update_Name_UpdatesNameAndTimestamp()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Old", 1000m, "BRL", false, FixedNow);
        var later = FixedNow.AddHours(1);

        source.Update("New Name", null, null, later);

        Assert.Equal("New Name", source.Name);
        Assert.Equal(later, source.UpdatedAtUtc);
    }

    [Fact]
    public void Update_Amount_UpdatesAmount()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        source.Update(null, 8000m, null, FixedNow.AddMinutes(1));

        Assert.Equal(8000m, source.Amount);
    }

    [Fact]
    public void Update_IsShared_UpdatesFlag()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        source.Update(null, null, true, FixedNow.AddMinutes(1));

        Assert.True(source.IsShared);
    }

    [Fact]
    public void Update_NegativeAmount_Throws()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        Assert.Throws<ArgumentException>(() => source.Update(null, -1m, null, FixedNow.AddMinutes(1)));
    }

    [Fact]
    public void Update_EmptyName_Throws()
    {
        var source = IncomeSource.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        Assert.Throws<ArgumentException>(() => source.Update("", null, null, FixedNow.AddMinutes(1)));
    }

    [Fact]
    public void CanBeEditedBy_Owner_ReturnsTrue()
    {
        var userId = Guid.NewGuid();
        var source = IncomeSource.Create(Guid.NewGuid(), userId, "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        Assert.True(source.CanBeEditedBy(userId));
    }

    [Fact]
    public void CanBeEditedBy_NonOwnerNonShared_ReturnsFalse()
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var source = IncomeSource.Create(Guid.NewGuid(), ownerId, "2026-05", "Salário", 1000m, "BRL", false, FixedNow);

        Assert.False(source.CanBeEditedBy(otherId));
    }

    [Fact]
    public void CanBeEditedBy_NonOwnerButShared_ReturnsTrue()
    {
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var source = IncomeSource.Create(Guid.NewGuid(), ownerId, "2026-05", "Renda compartilhada", 1000m, "BRL", true, FixedNow);

        Assert.True(source.CanBeEditedBy(otherId));
    }
}

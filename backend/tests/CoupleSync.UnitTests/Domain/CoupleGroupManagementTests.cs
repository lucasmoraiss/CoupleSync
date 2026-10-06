using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Domain;

[Trait("Category", "Domain")]
public sealed class CoupleGroupManagementTests
{
    private static readonly DateTime T0 = new(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);

    private static User NewUser(string name) => User.Create(EmailAddress.From($"{name}@example.com"), name, "hash", T0);

    [Fact]
    public void FirstMemberBecomesOwner_AndLaterMembersDoNot()
    {
        var couple = Couple.Create("ABC123", T0);
        var first = NewUser("first");
        var second = NewUser("second");

        couple.AddMember(first, T0);
        couple.AddMember(second, T0.AddDays(1));

        Assert.Equal(first.Id, couple.OwnerUserId);
    }

    [Fact]
    public void NewGroup_CodeIsValidForSevenDays()
    {
        var couple = Couple.Create("ABC123", T0);

        Assert.Equal(T0.AddDays(7), couple.JoinCodeExpiresAtUtc);
        Assert.False(couple.IsJoinCodeExpired(T0.AddDays(7).AddTicks(-1)));
        Assert.True(couple.IsJoinCodeExpired(T0.AddDays(7)));
    }

    [Theory]
    [InlineData("ABC123")]
    [InlineData("ABCD2345")]
    public void Create_AcceptsSixOrEightCharacterCodes(string code)
    {
        Assert.Equal(code, Couple.Create(code.ToLowerInvariant(), T0).JoinCode);
    }

    [Theory]
    [InlineData("ABCDE")]
    [InlineData("ABCDEFG")]
    [InlineData("ABCDEFGHI")]
    [InlineData("ABC-1234")]
    public void Create_RejectsOtherCodes(string code)
    {
        Assert.Throws<ArgumentException>(() => Couple.Create(code, T0));
    }

    [Fact]
    public void RegenerateJoinCode_ReplacesCodeAndRestartsValidity()
    {
        var couple = Couple.Create("ABC123", T0);

        couple.RegenerateJoinCode("ZYXW9876", T0.AddDays(6));

        Assert.Equal("ZYXW9876", couple.JoinCode);
        Assert.Equal(T0.AddDays(13), couple.JoinCodeExpiresAtUtc);
    }

    [Fact]
    public void RemoveMember_DetachesUserFromGroup()
    {
        var couple = Couple.Create("ABC123", T0);
        var owner = NewUser("owner");
        var other = NewUser("other");
        couple.AddMember(owner, T0);
        couple.AddMember(other, T0.AddDays(1));

        couple.RemoveMember(other, T0.AddDays(2));

        Assert.Null(other.CoupleId);
        Assert.Null(other.CoupleJoinedAtUtc);
        Assert.Single(couple.Members);
        Assert.Equal(owner.Id, couple.OwnerUserId);
    }

    [Fact]
    public void RemoveMember_WhenOwnerLeaves_PassesOwnershipToOldestRemainingMember()
    {
        var couple = Couple.Create("ABC123", T0);
        var owner = NewUser("owner");
        var oldest = NewUser("oldest");
        var newest = NewUser("newest");
        couple.AddMember(owner, T0);
        couple.AddMember(newest, T0.AddDays(5));
        couple.AddMember(oldest, T0.AddDays(2));

        couple.RemoveMember(owner, T0.AddDays(6));

        Assert.Equal(oldest.Id, couple.OwnerUserId);
    }

    [Fact]
    public void RemoveMember_WhenLastMemberLeaves_GroupHasNoOwnerAndCodeStopsWorking()
    {
        var couple = Couple.Create("ABC123", T0);
        var only = NewUser("only");
        couple.AddMember(only, T0);

        couple.RemoveMember(only, T0.AddDays(1));

        Assert.Null(couple.OwnerUserId);
        Assert.Empty(couple.Members);
        Assert.True(couple.IsJoinCodeExpired(T0.AddDays(1)));
    }

    [Fact]
    public void RemoveMember_WhenNotAMember_Throws()
    {
        var couple = Couple.Create("ABC123", T0);
        couple.AddMember(NewUser("a"), T0);

        Assert.Throws<InvalidOperationException>(() => couple.RemoveMember(NewUser("stranger"), T0));
    }

    [Fact]
    public void LeftUser_CanJoinAnotherGroup()
    {
        var first = Couple.Create("ABC123", T0);
        var user = NewUser("traveller");
        first.AddMember(user, T0);
        first.RemoveMember(user, T0.AddDays(1));

        var second = Couple.Create("XYZ789", T0);
        second.AddMember(user, T0.AddDays(2));

        Assert.Equal(second.Id, user.CoupleId);
    }
}

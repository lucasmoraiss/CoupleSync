using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;

namespace CoupleSync.UnitTests.Domain;

[Trait("Category", "Domain")]
public sealed class CoupleTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);

    private static Couple CreateCouple()
        => Couple.Create("ABC123", FixedNow);

    private static User CreateUser(string email)
        => User.Create(EmailAddress.From(email), "Test User", "hash", FixedNow);

    // ── AddMember ────────────────────────────────────────────────────────

    [Fact]
    public void AddMember_ThirdMember_SucceedsAndCountIsThree()
    {
        var couple = CreateCouple();
        var user1 = CreateUser("user1@example.com");
        var user2 = CreateUser("user2@example.com");
        var user3 = CreateUser("user3@example.com");

        couple.AddMember(user1, FixedNow);
        couple.AddMember(user2, FixedNow);
        couple.AddMember(user3, FixedNow);

        Assert.Equal(3, couple.Members.Count);
    }

    [Fact]
    public void AddMember_DuplicateMember_ThrowsInvalidOperationException()
    {
        var couple = CreateCouple();
        var user = CreateUser("user@example.com");

        couple.AddMember(user, FixedNow);

        var exception = Assert.Throws<InvalidOperationException>(() => couple.AddMember(user, FixedNow));
        Assert.Equal("User is already in this couple.", exception.Message);
    }

    [Fact]
    public void AddMember_NullUser_ThrowsArgumentNullException()
    {
        var couple = CreateCouple();

        Assert.Throws<ArgumentNullException>(() => couple.AddMember(null!, FixedNow));
    }

    [Fact]
    public void AddMember_FirstMember_AssignsCoupleIdToUser()
    {
        var couple = CreateCouple();
        var user = CreateUser("user@example.com");

        couple.AddMember(user, FixedNow);

        Assert.Equal(couple.Id, user.CoupleId);
    }
}

using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Couples;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.Couples;

public sealed class JoinCoupleCommandHandlerTests
{
    private static readonly DateTime FixedNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenUserNotFound_ShouldThrowUnauthorized()
    {
        var repo = new FakeCoupleRepository();
        var handler = new JoinCoupleCommandHandler(repo, new FixedDateTimeProvider(FixedNow), new StubJwtTokenService(), new FakeNotificationEventRepository(), NullLogger<JoinCoupleCommandHandler>.Instance);
        var command = new JoinCoupleCommand(Guid.NewGuid(), "ABC123");

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("UNAUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenUserAlreadyInCouple_ShouldThrowConflict()
    {
        var repo = new FakeCoupleRepository();
        var user = User.Create(EmailAddress.From("already@example.com"), "Already In", "hashed", FixedNow);
        var couple = Domain.Entities.Couple.Create("EXIST1", FixedNow);
        couple.AddMember(user, FixedNow);
        repo.Users.Add(user);
        repo.Couples.Add(couple);

        var handler = new JoinCoupleCommandHandler(repo, new FixedDateTimeProvider(FixedNow), new StubJwtTokenService(), new FakeNotificationEventRepository(), NullLogger<JoinCoupleCommandHandler>.Instance);
        var command = new JoinCoupleCommand(user.Id, "EXIST1");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("USER_ALREADY_IN_COUPLE", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenJoinCodeNotFound_ShouldThrowNotFound()
    {
        var repo = new FakeCoupleRepository();
        var user = User.Create(EmailAddress.From("nocouple@example.com"), "No Couple", "hashed", FixedNow);
        repo.Users.Add(user);

        var handler = new JoinCoupleCommandHandler(repo, new FixedDateTimeProvider(FixedNow), new StubJwtTokenService(), new FakeNotificationEventRepository(), NullLogger<JoinCoupleCommandHandler>.Instance);
        var command = new JoinCoupleCommand(user.Id, "NOPEX1");

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("COUPLE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenCoupleFull_ShouldThrowConflict()
    {
        var repo = new FakeCoupleRepository();

        // Create two existing members that fill the couple
        var member1 = User.Create(EmailAddress.From("member1@example.com"), "Member One", "hashed", FixedNow);
        var member2 = User.Create(EmailAddress.From("member2@example.com"), "Member Two", "hashed", FixedNow);
        var fullCouple = Domain.Entities.Couple.Create("FULL01", FixedNow);
        fullCouple.AddMember(member1, FixedNow);
        fullCouple.AddMember(member2, FixedNow);

        // Third user trying to join
        var joiner = User.Create(EmailAddress.From("joiner@example.com"), "Joiner", "hashed", FixedNow);

        repo.Users.Add(member1);
        repo.Users.Add(member2);
        repo.Users.Add(joiner);
        repo.Couples.Add(fullCouple);

        var handler = new JoinCoupleCommandHandler(repo, new FixedDateTimeProvider(FixedNow), new StubJwtTokenService(), new FakeNotificationEventRepository(), NullLogger<JoinCoupleCommandHandler>.Instance);
        var command = new JoinCoupleCommand(joiner.Id, "FULL01");

        var ex = await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("COUPLE_FULL", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_HappyPath_ShouldJoinCoupleAndReturnMembers()
    {
        var repo = new FakeCoupleRepository();

        var owner = User.Create(EmailAddress.From("owner@example.com"), "Owner", "hashed", FixedNow);
        var couple = Domain.Entities.Couple.Create("JOIN01", FixedNow);
        couple.AddMember(owner, FixedNow);

        var joiner = User.Create(EmailAddress.From("joiner2@example.com"), "Joiner", "hashed", FixedNow);

        repo.Users.Add(owner);
        repo.Users.Add(joiner);
        repo.Couples.Add(couple);

        var handler = new JoinCoupleCommandHandler(repo, new FixedDateTimeProvider(FixedNow), new StubJwtTokenService(), new FakeNotificationEventRepository(), NullLogger<JoinCoupleCommandHandler>.Instance);
        var command = new JoinCoupleCommand(joiner.Id, "JOIN01");

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(couple.Id, result.CoupleId);
        Assert.Equal(2, result.Members.Count);
        Assert.Equal(1, repo.SaveChangesCalls);
    }
}

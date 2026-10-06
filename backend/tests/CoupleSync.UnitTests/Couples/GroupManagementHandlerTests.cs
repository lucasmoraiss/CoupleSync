using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Common.Options;
using CoupleSync.Application.Couples;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CoupleSync.UnitTests.Couples;

public sealed class GroupManagementHandlerTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeCoupleRepository _couples = new();
    private readonly FakeAuthRepository _auth = new();
    private readonly Couple _group = Couple.Create("ABC123", T0);
    private readonly User _owner = NewUser("owner");
    private readonly User _member = NewUser("member");

    public GroupManagementHandlerTests()
    {
        _group.AddMember(_owner, T0);
        _group.AddMember(_member, T0.AddDays(1));
        _couples.Users.AddRange([_owner, _member]);
        _auth.Users.AddRange([_owner, _member]);
        _couples.Couples.Add(_group);
        _auth.RefreshTokens.Add(RefreshToken.CreateForUser(_member.Id, "old-hash", T0.AddDays(30), T0));
    }

    private static User NewUser(string name) => User.Create(EmailAddress.From($"{name}@example.com"), name, "hash", T0);

    private LeaveCoupleCommandHandler LeaveHandler(DateTime now) => new(
        _couples, _auth, new FixedDateTimeProvider(now), new StubJwtTokenService { Token = "no-group-token" },
        new Sha256TokenHasher(), Options.Create(new JwtOptions()));

    private RemoveCoupleMemberCommandHandler RemoveHandler(DateTime now) => new(
        _couples, new StubMembership(_group), new FixedDateTimeProvider(now));

    private RegenerateJoinCodeCommandHandler RegenerateHandler(DateTime now, string code = "NEWCODE8") => new(
        _couples, new StubMembership(_group), new FixedCode(code), new FixedDateTimeProvider(now));

    private JoinCoupleCommandHandler JoinHandler(DateTime now) => new(
        _couples, new FixedDateTimeProvider(now), new StubJwtTokenService(), new FakeNotificationEventRepository(),
        NullLogger<JoinCoupleCommandHandler>.Instance);

    // leave

    [Fact]
    public async Task Leave_DetachesUser_ReplacesRefreshToken_AndStopsDeliveries()
    {
        var result = await LeaveHandler(T0.AddDays(2)).HandleAsync(new LeaveCoupleCommand(_member.Id), default);

        Assert.Null(_member.CoupleId);
        Assert.DoesNotContain(_member, _group.Members);
        Assert.Equal("no-group-token", result.AccessToken);
        var stored = Assert.Single(_auth.RefreshTokens, t => t.UserId == _member.Id);
        Assert.NotEqual("old-hash", stored.TokenHash);
        Assert.Equal(new Sha256TokenHasher().Hash(result.RefreshToken), stored.TokenHash);
        Assert.Contains((_member.Id, _group.Id), _couples.StoppedDeliveries);
        Assert.Equal(1, _couples.SaveChangesCalls);
    }

    [Fact]
    public async Task Leave_WhenOwner_PassesOwnershipToOldestRemaining()
    {
        await LeaveHandler(T0.AddDays(2)).HandleAsync(new LeaveCoupleCommand(_owner.Id), default);

        Assert.Equal(_member.Id, _group.OwnerUserId);
    }

    [Fact]
    public async Task Leave_WhenUserHasNoGroup_ThrowsNotFound()
    {
        var loner = NewUser("loner");
        _couples.Users.Add(loner);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => LeaveHandler(T0).HandleAsync(new LeaveCoupleCommand(loner.Id), default));
        Assert.Equal("COUPLE_NOT_FOUND", ex.Code);
    }

    // remove

    [Fact]
    public async Task Remove_ByOwner_DetachesMember_RevokesRefreshToken_AndStopsDeliveries()
    {
        await RemoveHandler(T0.AddDays(2)).HandleAsync(new RemoveCoupleMemberCommand(_owner.Id, _member.Id), default);

        Assert.Null(_member.CoupleId);
        Assert.Equal([_member.Id], _couples.RevokedRefreshTokenUserIds);
        Assert.Contains((_member.Id, _group.Id), _couples.StoppedDeliveries);
        Assert.Equal(1, _couples.SaveChangesCalls);
    }

    [Fact]
    public async Task Remove_ByNonOwner_IsForbidden_AndChangesNothing()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => RemoveHandler(T0).HandleAsync(new RemoveCoupleMemberCommand(_member.Id, _owner.Id), default));

        Assert.Equal("NOT_COUPLE_OWNER", ex.Code);
        Assert.Equal(_group.Id, _owner.CoupleId);
        Assert.Equal(0, _couples.SaveChangesCalls);
    }

    [Fact]
    public async Task Remove_Self_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => RemoveHandler(T0).HandleAsync(new RemoveCoupleMemberCommand(_owner.Id, _owner.Id), default));
        Assert.Equal("CANNOT_REMOVE_SELF", ex.Code);
    }

    [Fact]
    public async Task Remove_UserOutsideTheGroup_IsNotFound()
    {
        var stranger = NewUser("stranger");
        _couples.Users.Add(stranger);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => RemoveHandler(T0).HandleAsync(new RemoveCoupleMemberCommand(_owner.Id, stranger.Id), default));
        Assert.Equal("MEMBER_NOT_FOUND", ex.Code);
    }

    // regenerate

    [Fact]
    public async Task Regenerate_ByOwner_ReplacesCodeAndGivesSevenDays()
    {
        var now = T0.AddDays(3);

        var result = await RegenerateHandler(now).HandleAsync(new RegenerateJoinCodeCommand(_owner.Id), default);

        Assert.Equal("NEWCODE8", result.JoinCode);
        Assert.Equal(now.AddDays(7), result.JoinCodeExpiresAtUtc);
        Assert.Equal("NEWCODE8", _group.JoinCode);
        Assert.Null(await _couples.FindByJoinCodeAsync("ABC123", default));
    }

    [Fact]
    public async Task Regenerate_ByNonOwner_IsForbidden_AndKeepsTheCode()
    {
        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => RegenerateHandler(T0).HandleAsync(new RegenerateJoinCodeCommand(_member.Id), default));

        Assert.Equal("NOT_COUPLE_OWNER", ex.Code);
        Assert.Equal("ABC123", _group.JoinCode);
    }

    // join with expiry

    [Fact]
    public async Task Join_WithExpiredCode_ThrowsExpiredAndDoesNotAddMember()
    {
        var joiner = NewUser("joiner");
        _couples.Users.Add(joiner);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => JoinHandler(T0.AddDays(7)).HandleAsync(new JoinCoupleCommand(joiner.Id, "abc123"), default));

        Assert.Equal("JOIN_CODE_EXPIRED", ex.Code);
        Assert.Equal(410, ex.StatusCode);
        Assert.Contains("código", ex.Message);
        Assert.Null(joiner.CoupleId);
    }

    [Fact]
    public async Task Join_WithSixCharacterCodeBeforeExpiry_StillWorks()
    {
        var joiner = NewUser("joiner");
        _couples.Users.Add(joiner);

        var result = await JoinHandler(T0.AddDays(7).AddMinutes(-1))
            .HandleAsync(new JoinCoupleCommand(joiner.Id, " abc123 "), default);

        Assert.Equal(_group.Id, result.CoupleId);
    }

    [Fact]
    public async Task Join_WithRenewedCode_OldCodeIsGone()
    {
        await RegenerateHandler(T0.AddDays(1)).HandleAsync(new RegenerateJoinCodeCommand(_owner.Id), default);
        var joiner = NewUser("joiner");
        _couples.Users.Add(joiner);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => JoinHandler(T0.AddDays(1)).HandleAsync(new JoinCoupleCommand(joiner.Id, "ABC123"), default));
        Assert.Equal("COUPLE_NOT_FOUND", ex.Code);

        var ok = await JoinHandler(T0.AddDays(1)).HandleAsync(new JoinCoupleCommand(joiner.Id, "NEWCODE8"), default);
        Assert.Equal(_group.Id, ok.CoupleId);
    }

    [Fact]
    public async Task Join_AfterEveryoneLeft_IsRefusedBecauseCodeIsExpired()
    {
        await LeaveHandler(T0.AddDays(2)).HandleAsync(new LeaveCoupleCommand(_member.Id), default);
        await LeaveHandler(T0.AddDays(2)).HandleAsync(new LeaveCoupleCommand(_owner.Id), default);
        var joiner = NewUser("joiner");
        _couples.Users.Add(joiner);

        var ex = await Assert.ThrowsAsync<AppException>(
            () => JoinHandler(T0.AddDays(3)).HandleAsync(new JoinCoupleCommand(joiner.Id, "ABC123"), default));
        Assert.Equal("JOIN_CODE_EXPIRED", ex.Code);
    }

    private sealed class StubMembership : ICoupleMembership
    {
        private readonly Couple _couple;

        public StubMembership(Couple couple) => _couple = couple;

        public Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
            => Task.FromResult(coupleId == _couple.Id && _couple.Members.Any(m => m.Id == userId));

        public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
            => Task.FromResult(coupleId == _couple.Id && _couple.OwnerUserId == userId);
    }

    private sealed class FixedCode : ICoupleJoinCodeGenerator
    {
        private readonly string _code;

        public FixedCode(string code) => _code = code;

        public string Generate() => _code;
    }
}

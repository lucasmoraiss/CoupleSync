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

/// <summary>A user in several groups: active group, switching, leaving, removal, the limit and the locking order.</summary>
public sealed class MultiGroupHandlerTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeCoupleRepository _couples = new();
    private readonly FakeAuthRepository _auth = new();
    private readonly StubJwtTokenService _jwt = new();
    private readonly User _ana = NewUser("Ana");
    private readonly User _bruno = NewUser("Bruno");
    private readonly Couple _anas = Couple.Create("ANAS1234", T0);
    private readonly Couple _shared = Couple.Create("SHARED12", T0);

    public MultiGroupHandlerTests()
    {
        // Ana owns her own group (joined first) and is a plain member of the one Bruno owns (joined later, active).
        _anas.AddMember(_ana, T0);
        _shared.AddMember(_bruno, T0.AddDays(1));
        _shared.AddMember(_ana, T0.AddDays(2));
        _couples.Users.AddRange([_ana, _bruno]);
        _auth.Users.AddRange([_ana, _bruno]);
        _couples.Couples.AddRange([_anas, _shared]);
        _auth.RefreshTokens.Add(RefreshToken.CreateForUser(_ana.Id, "old-hash", T0.AddDays(30), T0));
    }

    private static User NewUser(string name) => User.Create(EmailAddress.From($"{name}@example.com"), name, "hash", T0);

    private static IOptions<JwtOptions> Jwt => Options.Create(new JwtOptions());

    private LeaveCoupleCommandHandler Leave => new(_couples, _auth, new FixedDateTimeProvider(T0.AddDays(3)), _jwt, new Sha256TokenHasher(), Jwt);

    private SwitchCoupleCommandHandler Switch => new(_couples, _auth, new FixedDateTimeProvider(T0.AddDays(3)), _jwt, new Sha256TokenHasher(), Jwt);

    private RemoveCoupleMemberCommandHandler Remove => new(_couples, new MembershipOf(_couples), new FixedDateTimeProvider(T0.AddDays(3)));

    private JoinCoupleCommandHandler Join => new(
        _couples, new FixedDateTimeProvider(T0.AddDays(3)), _jwt, new FakeNotificationEventRepository(),
        NullLogger<JoinCoupleCommandHandler>.Instance, _auth, new Sha256TokenHasher(), Jwt);

    private CreateCoupleCommandHandler Create(string code) => new(
        _couples, new FixedCode(code), new FixedDateTimeProvider(T0.AddDays(3)), _jwt, _auth, new Sha256TokenHasher(), Jwt);

    [Fact]
    public void AddMember_MakesTheGroupTheUsersActiveOne_AndOnlyTheFirstMemberIsOwner()
    {
        Assert.Equal(_shared.Id, _ana.ActiveCoupleId);
        Assert.Equal(T0.AddDays(2), _ana.ActiveCoupleJoinedAtUtc);
        Assert.Equal(CoupleRole.Owner, _shared.Members.Single(m => m.UserId == _bruno.Id).Role);
        Assert.Equal(CoupleRole.Member, _shared.Members.Single(m => m.UserId == _ana.Id).Role);
        Assert.Equal(_bruno.Id, _shared.OwnerUserId);
        Assert.True(_anas.HasMember(_ana.Id));
    }

    [Fact]
    public void OwnerLeaving_HandsTheOwnerRoleToTheOldestRemainingMember()
    {
        _shared.RemoveMember(_bruno, T0.AddDays(3));

        var remaining = Assert.Single(_shared.Members);
        Assert.Equal(_ana.Id, remaining.UserId);
        Assert.Equal(CoupleRole.Owner, remaining.Role);
        Assert.Equal(_ana.Id, _shared.OwnerUserId);
    }

    [Fact]
    public async Task Switch_ToAnotherOfTheUsersGroups_MovesTheActiveGroup_AndReplacesTheTokens()
    {
        var result = await Switch.HandleAsync(new SwitchCoupleCommand(_ana.Id, _anas.Id), default);

        Assert.Equal(_anas.Id, result.CoupleId);
        Assert.Equal(_anas.Id, _ana.ActiveCoupleId);
        Assert.Equal(T0, _ana.ActiveCoupleJoinedAtUtc);
        Assert.Equal([_anas.Id], _jwt.IssuedForCoupleIds);
        var stored = Assert.Single(_auth.RefreshTokens, t => t.UserId == _ana.Id);
        Assert.Equal(new Sha256TokenHasher().Hash(result.RefreshToken), stored.TokenHash);
        Assert.Equal([$"begin:{_ana.Id}", "save", "commit"], _couples.Steps);
    }

    [Fact]
    public async Task Switch_ToAGroupTheUserIsNotIn_IsNotFound_AndChangesNothing()
    {
        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);

        foreach (var target in new[] { brunos.Id, Guid.NewGuid() })
        {
            var ex = await Assert.ThrowsAsync<NotFoundException>(
                () => Switch.HandleAsync(new SwitchCoupleCommand(_ana.Id, target), default));
            Assert.Equal("COUPLE_NOT_FOUND", ex.Code);
        }

        Assert.Equal(_shared.Id, _ana.ActiveCoupleId);
        Assert.Empty(_jwt.IssuedForCoupleIds);
        Assert.Equal("old-hash", Assert.Single(_auth.RefreshTokens).TokenHash);
        Assert.Equal(0, _couples.Commits);
    }

    [Fact]
    public async Task Leave_TheActiveGroup_ActivatesTheOldestRemainingGroup_AndKeepsTheDevicesUnderIt()
    {
        var result = await Leave.HandleAsync(new LeaveCoupleCommand(_ana.Id), default);

        Assert.False(_shared.HasMember(_ana.Id));
        Assert.Equal(_anas.Id, result.ActiveCoupleId);
        Assert.Equal(_anas.Id, _ana.ActiveCoupleId);
        Assert.Equal([_anas.Id], _jwt.IssuedForCoupleIds);
        Assert.Equal([(_ana.Id, _shared.Id, (Guid?)_anas.Id)], _couples.StoppedDeliveries);
        Assert.Equal([$"begin:{_ana.Id}", $"lock:{_shared.Id}", "save", "commit"], _couples.Steps);
    }

    [Fact]
    public async Task Leave_AnotherGroupByItsId_LeavesTheActiveGroupAsItIs()
    {
        var result = await Leave.HandleAsync(new LeaveCoupleCommand(_ana.Id, _anas.Id), default);

        Assert.False(_anas.HasMember(_ana.Id));
        Assert.Equal(_shared.Id, result.ActiveCoupleId);
        Assert.Equal(_shared.Id, _ana.ActiveCoupleId);
        Assert.Null(_anas.OwnerUserId);
        Assert.True(_anas.IsJoinCodeExpired(T0.AddDays(3)));
        Assert.Equal([(_ana.Id, _anas.Id, (Guid?)_shared.Id)], _couples.StoppedDeliveries);
    }

    [Fact]
    public async Task Leave_AGroupTheUserIsNotIn_IsNotFound_AndThatGroupIsUntouched()
    {
        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Leave.HandleAsync(new LeaveCoupleCommand(_ana.Id, brunos.Id), default));

        Assert.Equal("COUPLE_NOT_FOUND", ex.Code);
        Assert.True(brunos.HasMember(_bruno.Id));
        Assert.Equal(_bruno.Id, brunos.OwnerUserId);
        Assert.Equal(0, _couples.SaveChangesCalls);
    }

    [Fact]
    public async Task Remove_FromTheMembersActiveGroup_LeavesThemWithNoActiveGroup_EvenWithOtherGroups()
    {
        await Remove.HandleAsync(new RemoveCoupleMemberCommand(_bruno.Id, _ana.Id), default);

        Assert.False(_shared.HasMember(_ana.Id));
        Assert.True(_anas.HasMember(_ana.Id));
        Assert.Null(_ana.ActiveCoupleId);
        Assert.Equal([_ana.Id], _couples.RevokedRefreshTokenUserIds);
        Assert.Equal([(_ana.Id, _shared.Id, (Guid?)_anas.Id)], _couples.StoppedDeliveries);
        // The member being removed is locked first, then the group.
        Assert.Equal([$"begin:{_ana.Id}", $"lock:{_shared.Id}", "save", "commit"], _couples.Steps);
    }

    [Fact]
    public async Task Remove_FromAGroupThatIsNotTheMembersActiveOne_DoesNotDisturbTheirSession()
    {
        _ana.SetActiveCouple(_anas.Id, T0);

        await Remove.HandleAsync(new RemoveCoupleMemberCommand(_bruno.Id, _ana.Id), default);

        Assert.False(_shared.HasMember(_ana.Id));
        Assert.Equal(_anas.Id, _ana.ActiveCoupleId);
        Assert.Empty(_couples.RevokedRefreshTokenUserIds);
        Assert.Equal([(_ana.Id, _shared.Id, (Guid?)_anas.Id)], _couples.StoppedDeliveries);
    }

    [Fact]
    public async Task Remove_ActsOnTheOwnersActiveGroupOnly()
    {
        // Ana owns her own group, but her active group is the shared one, where she is not the owner.
        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => Remove.HandleAsync(new RemoveCoupleMemberCommand(_ana.Id, _bruno.Id), default));

        Assert.Equal("NOT_COUPLE_OWNER", ex.Code);
        Assert.True(_shared.HasMember(_bruno.Id));
    }

    [Fact]
    public async Task Create_AtTheLimit_IsRefused_AndBelowItTheNewGroupBecomesActive()
    {
        for (var i = 0; i < 3; i++)
        {
            await Create($"GROUP00{i}").HandleAsync(new CreateCoupleCommand(_ana.Id), default);
        }

        Assert.Equal(User.MaxGroups, _couples.Couples.Count(c => c.HasMember(_ana.Id)));
        var fifth = _couples.Couples.Single(c => c.JoinCode == "GROUP002");
        Assert.Equal(fifth.Id, _ana.ActiveCoupleId);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => Create("GROUP999").HandleAsync(new CreateCoupleCommand(_ana.Id), default));
        Assert.Equal("GROUP_LIMIT_REACHED", ex.Code);
        Assert.Equal(User.MaxGroups, _couples.Couples.Count(c => c.HasMember(_ana.Id)));
        Assert.Equal(fifth.Id, _ana.ActiveCoupleId);
    }

    [Fact]
    public async Task Join_AtTheLimit_IsRefused()
    {
        for (var i = 0; i < 3; i++)
        {
            await Create($"GROUP00{i}").HandleAsync(new CreateCoupleCommand(_ana.Id), default);
        }

        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => Join.HandleAsync(new JoinCoupleCommand(_ana.Id, "BRUNO123"), default));

        Assert.Equal("GROUP_LIMIT_REACHED", ex.Code);
        Assert.False(brunos.HasMember(_ana.Id));
    }

    [Fact]
    public async Task Join_LocksTheUserThenTheGroup_BeforeDecidingAndSaving()
    {
        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);

        var result = await Join.HandleAsync(new JoinCoupleCommand(_ana.Id, "bruno123"), default);

        Assert.Equal(brunos.Id, result.CoupleId);
        Assert.Equal(brunos.Id, _ana.ActiveCoupleId);
        Assert.Equal(3, _couples.Couples.Count(c => c.HasMember(_ana.Id)));
        Assert.Equal([$"begin:{_ana.Id}", $"lock:{brunos.Id}", "save", "commit"], _couples.Steps.Take(4));
    }

    [Fact]
    public async Task MyGroups_ReturnsOnlyTheUsersGroups_OldestFirst_AndIgnoresAStaleActivePointer()
    {
        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);
        var handler = new GetMyGroupsQueryHandler(_couples);

        var result = await handler.HandleAsync(new GetMyGroupsQuery(_ana.Id), default);

        Assert.Equal([_anas.Id, _shared.Id], result.Groups.Select(g => g.CoupleId));
        Assert.Equal(_shared.Id, result.ActiveCoupleId);
        Assert.Equal(["Grupo só seu", "Grupo com Bruno"], result.Groups.Select(g => g.Name));
        Assert.Equal([true, false], result.Groups.Select(g => g.IsOwner));
        Assert.Equal(User.MaxGroups, result.MaxGroups);

        _ana.SetActiveCouple(brunos.Id, T0); // a pointer to a group she is not in grants and shows nothing
        var stale = await handler.HandleAsync(new GetMyGroupsQuery(_ana.Id), default);
        Assert.Null(stale.ActiveCoupleId);
        Assert.DoesNotContain(stale.Groups, g => g.CoupleId == brunos.Id);
        Assert.DoesNotContain(stale.Groups, g => g.IsActive);
    }

    [Fact]
    public async Task CoupleMe_WithAnActivePointerToAGroupTheUserIsNotIn_IsNotFound()
    {
        var brunos = Couple.Create("BRUNO123", T0);
        brunos.AddMember(_bruno, T0);
        _couples.Couples.Add(brunos);
        _ana.SetActiveCouple(brunos.Id, T0);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => new GetCoupleMeQueryHandler(_couples).HandleAsync(new GetCoupleMeQuery(_ana.Id), default));
        Assert.Equal("COUPLE_NOT_FOUND", ex.Code);
    }

    [Theory]
    [InlineData("Grupo só seu")]
    [InlineData("Grupo com Ana", "Ana Souza")]
    [InlineData("Grupo com Ana e Bruno", "Ana", " Bruno Lima ")]
    [InlineData("Grupo com Ana, Bruno e mais 2", "Ana", "Bruno", "Carla", "Davi")]
    public void GroupLabel_NamesTheGroupAfterTheOtherMembers(string expected, params string[] others)
    {
        Assert.Equal(expected, GroupLabel.For(others));
    }

    [Fact]
    public async Task PushTitle_NamesTheGroupOnlyForSomeoneInSeveralGroups()
    {
        var anasGroups = await _couples.GetGroupsOfUserAsync(_ana.Id, default);
        var brunosGroups = await _couples.GetGroupsOfUserAsync(_bruno.Id, default);

        Assert.Equal("Transação de valor alto · Grupo com Bruno", GroupLabel.PushTitle("Transação de valor alto", _shared.Id, _ana.Id, anasGroups));
        Assert.Equal("Transação de valor alto · Grupo só seu", GroupLabel.PushTitle("Transação de valor alto", _anas.Id, _ana.Id, anasGroups));
        Assert.Equal("Transação de valor alto", GroupLabel.PushTitle("Transação de valor alto", _shared.Id, _bruno.Id, brunosGroups));
    }

    private sealed class MembershipOf : ICoupleMembership
    {
        private readonly FakeCoupleRepository _couples;

        public MembershipOf(FakeCoupleRepository couples) => _couples = couples;

        public Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
            => Task.FromResult(_couples.Couples.Any(c => c.Id == coupleId && c.HasMember(userId)));

        public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
            => Task.FromResult(_couples.Couples.Any(c => c.Id == coupleId
                && c.Members.Any(m => m.UserId == userId && m.Role == CoupleRole.Owner)));
    }

    private sealed class FixedCode : ICoupleJoinCodeGenerator
    {
        private readonly string _code;

        public FixedCode(string code) => _code = code;

        public string Generate() => _code;
    }
}

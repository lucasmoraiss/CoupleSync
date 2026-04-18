using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Couples;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Couples;

public sealed class CreateCoupleCommandHandlerTests
{
    private static readonly DateTime FixedNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task HandleAsync_WhenUserNotFound_ShouldThrowUnauthorized()
    {
        var repo = new FakeCoupleRepository();
        var handler = new CreateCoupleCommandHandler(repo, new FixedJoinCodeGenerator("ABC123"), new FixedDateTimeProvider(FixedNow));
        var command = new CreateCoupleCommand(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("UNAUTHORIZED", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenUserAlreadyInCouple_ShouldThrowConflict()
    {
        var repo = new FakeCoupleRepository();
        var user = User.Create(EmailAddress.From("user@example.com"), "Test User", "hashed", FixedNow);
        var existingCouple = Domain.Entities.Couple.Create("XYZ999", FixedNow);
        existingCouple.AddMember(user, FixedNow);
        repo.Users.Add(user);
        repo.Couples.Add(existingCouple);

        var handler = new CreateCoupleCommandHandler(repo, new FixedJoinCodeGenerator("NEWCOD"), new FixedDateTimeProvider(FixedNow));
        var command = new CreateCoupleCommand(user.Id);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("USER_ALREADY_IN_COUPLE", ex.Code);
    }

    [Fact]
    public async Task HandleAsync_HappyPath_ShouldCreateCoupleAndReturnResult()
    {
        var repo = new FakeCoupleRepository();
        var user = User.Create(EmailAddress.From("happy@example.com"), "Happy User", "hashed", FixedNow);
        repo.Users.Add(user);

        var handler = new CreateCoupleCommandHandler(repo, new FixedJoinCodeGenerator("AB1234"), new FixedDateTimeProvider(FixedNow));
        var command = new CreateCoupleCommand(user.Id);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.CoupleId);
        Assert.Equal(6, result.JoinCode.Length);
        Assert.Equal(1, repo.SaveChangesCalls);
    }

    [Fact]
    public async Task HandleAsync_WhenAllJoinCodeAttemptsCollide_ShouldThrowAppException()
    {
        var repo = new FakeCoupleRepository();
        var user = User.Create(EmailAddress.From("collision@example.com"), "Collision User", "hashed", FixedNow);
        repo.Users.Add(user);

        // Pre-seed the code so JoinCodeExistsAsync always returns true
        var existingCouple = Domain.Entities.Couple.Create("FIXED1", FixedNow);
        repo.Couples.Add(existingCouple);

        // Generator always returns the same code that already exists
        var handler = new CreateCoupleCommandHandler(repo, new FixedJoinCodeGenerator("FIXED1"), new FixedDateTimeProvider(FixedNow));
        var command = new CreateCoupleCommand(user.Id);

        var ex = await Assert.ThrowsAsync<AppException>(() => handler.HandleAsync(command, CancellationToken.None));
        Assert.Equal("COUPLE_CODE_GENERATION_FAILED", ex.Code);
        Assert.Equal(500, ex.StatusCode);
    }

    private sealed class FixedJoinCodeGenerator : ICoupleJoinCodeGenerator
    {
        private readonly string _code;

        public FixedJoinCodeGenerator(string code) => _code = code;

        public string Generate() => _code;
    }
}

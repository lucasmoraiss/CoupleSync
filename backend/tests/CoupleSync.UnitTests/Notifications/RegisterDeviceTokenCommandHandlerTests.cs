using CoupleSync.Application.Notification.Commands;
using CoupleSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CoupleSync.UnitTests.Notifications;

public sealed class RegisterDeviceTokenCommandHandlerTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid CoupleId = Guid.NewGuid();

    private static FixedDateTimeProvider FixedClock() => new(new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));

    private static DbUpdateException UniqueViolation() =>
        new("save failed", new Exception("23505: duplicate key value violates unique constraint \"IX_DeviceTokens_Token\""));

    [Fact]
    public async Task HandleAsync_LosingTheUniqueIndexRace_RetriesOnceAndSucceeds()
    {
        var repo = new FakeDeviceTokenRepository();
        repo.SaveFailures.Enqueue(UniqueViolation());
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock());

        await handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None);

        Assert.Equal(2, repo.SaveCalls);
        Assert.Equal(1, repo.DiscardCalls);
        Assert.Single(repo.All);
    }

    [Fact]
    public async Task HandleAsync_UniqueViolationTwice_PropagatesTheSecondFailure()
    {
        var repo = new FakeDeviceTokenRepository();
        repo.SaveFailures.Enqueue(UniqueViolation());
        repo.SaveFailures.Enqueue(UniqueViolation());
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock());

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None));

        Assert.Equal(2, repo.SaveCalls);
    }

    [Fact]
    public async Task HandleAsync_OtherDatabaseFailure_IsNotRetried()
    {
        var repo = new FakeDeviceTokenRepository();
        repo.SaveFailures.Enqueue(new DbUpdateException("save failed", new Exception("connection reset")));
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock());

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None));

        Assert.Equal(1, repo.SaveCalls);
        Assert.Equal(0, repo.DiscardCalls);
    }
}

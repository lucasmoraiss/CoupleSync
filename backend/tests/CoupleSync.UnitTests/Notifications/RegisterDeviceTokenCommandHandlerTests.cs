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
        var delays = new DelayRecorder();
        repo.SaveFailures.Enqueue(UniqueViolation());
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock(), delays.Record);

        await handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None);

        Assert.Equal(2, repo.SaveCalls);
        Assert.Equal(1, repo.DiscardCalls);
        Assert.Single(repo.All);
    }

    [Fact]
    public async Task HandleAsync_UniqueViolationTwice_RetriesAgain_AndSucceedsWhenTheRaceEnds()
    {
        var repo = new FakeDeviceTokenRepository();
        var delays = new DelayRecorder();
        repo.SaveFailures.Enqueue(UniqueViolation());
        repo.SaveFailures.Enqueue(UniqueViolation());
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock(), delays.Record);

        await handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None);

        Assert.Equal(3, repo.SaveCalls);
        Assert.Equal(2, repo.DiscardCalls);
        Assert.Equal(3, repo.BeginCalls);
        Assert.Equal(1, repo.CommitCalls);
        Assert.Equal(2, delays.Count); // recorded, not slept
    }

    [Fact]
    public async Task HandleAsync_ARowAlreadyDeletedByAnotherRequest_IsRetried()
    {
        var repo = new FakeDeviceTokenRepository();
        var delays = new DelayRecorder();
        repo.SaveFailures.Enqueue(new DbUpdateConcurrencyException("0 rows affected", new Exception("expected 1")));
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock(), delays.Record);

        await handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None);

        Assert.Equal(2, repo.SaveCalls);
    }

    [Fact]
    public async Task HandleAsync_AlwaysLosingTheRace_EndsInAConflict_NotAServerError()
    {
        var repo = new FakeDeviceTokenRepository();
        var delays = new DelayRecorder();
        for (var i = 0; i < 20; i++) repo.SaveFailures.Enqueue(UniqueViolation());
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock(), delays.Record);

        var ex = await Assert.ThrowsAsync<CoupleSync.Application.Common.Exceptions.ConflictException>(() =>
            handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None));

        Assert.Equal("DEVICE_TOKEN_CONFLICT", ex.Code);
        Assert.Equal(8, repo.SaveCalls);
        Assert.Equal(7, delays.Count);
        Assert.Equal(0, repo.CommitCalls);
    }

    [Fact]
    public async Task HandleAsync_OtherDatabaseFailure_IsNotRetried()
    {
        var repo = new FakeDeviceTokenRepository();
        var delays = new DelayRecorder();
        repo.SaveFailures.Enqueue(new DbUpdateException("save failed", new Exception("connection reset")));
        var handler = new RegisterDeviceTokenCommandHandler(repo, FixedClock(), delays.Record);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            handler.HandleAsync(new RegisterDeviceTokenCommand(UserId, CoupleId, "fcm-1"), CancellationToken.None));

        Assert.Equal(1, repo.SaveCalls);
        Assert.Equal(0, repo.DiscardCalls);
    }

    private sealed class DelayRecorder
    {
        public int Count { get; private set; }

        public Task Record(TimeSpan delay, CancellationToken ct)
        {
            Count++;
            return Task.CompletedTask;
        }
    }
}

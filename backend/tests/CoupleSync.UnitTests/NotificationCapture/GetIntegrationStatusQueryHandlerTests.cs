using CoupleSync.Application.NotificationCapture;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.NotificationCapture;

public sealed class GetIntegrationStatusQueryHandlerTests
{
    private static readonly Guid CoupleId = Guid.NewGuid();

    private static GetIntegrationStatusQueryHandler BuildHandler(
        FakeNotificationCaptureRepository repo,
        FixedDateTimeProvider? dateTimeProvider = null)
    {
        return new GetIntegrationStatusQueryHandler(
            repo,
            dateTimeProvider ?? new FixedDateTimeProvider(new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc)));
    }

    private static TransactionEventIngest CreateEvent(
        IngestStatus status = IngestStatus.Accepted,
        DateTime? createdAt = null,
        string? errorMessage = null,
        Guid? coupleId = null)
    {
        var evt = TransactionEventIngest.Create(
            coupleId ?? CoupleId,
            Guid.NewGuid(),
            "NUBANK",
            100m,
            "BRL",
            DateTime.UtcNow.AddMinutes(-10),
            "Test",
            "TestMerchant",
            null,
            createdAt ?? new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc));

        if (status == IngestStatus.Duplicate)
            evt.MarkDuplicate();
        else if (status == IngestStatus.Rejected)
            evt.MarkRejected(errorMessage ?? "Test error");

        return evt;
    }

    [Fact]
    public async Task HandleAsync_NoEvents_ReturnsInactiveWithZeroCounts()
    {
        var repo = new FakeNotificationCaptureRepository();
        var handler = BuildHandler(repo);

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Null(result.LastEventAtUtc);
        Assert.Null(result.LastErrorAtUtc);
        Assert.Null(result.LastErrorMessage);
        Assert.Null(result.RecoveryHint);
        Assert.Equal(0, result.TotalAccepted);
        Assert.Equal(0, result.TotalDuplicate);
        Assert.Equal(0, result.TotalRejected);
    }

    [Fact]
    public async Task HandleAsync_AllAcceptedEvents_ReturnsActiveWithCorrectCounts()
    {
        var repo = new FakeNotificationCaptureRepository();
        var now = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var handler = BuildHandler(repo, new FixedDateTimeProvider(now));

        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-1)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-2)));

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Equal(2, result.TotalAccepted);
        Assert.Equal(0, result.TotalDuplicate);
        Assert.Equal(0, result.TotalRejected);
        Assert.Null(result.LastErrorAtUtc);
        Assert.Null(result.LastErrorMessage);
        Assert.Null(result.RecoveryHint);
    }

    [Fact]
    public async Task HandleAsync_HasRejectedEvent_ReturnsLastErrorAndRecoveryHint()
    {
        var repo = new FakeNotificationCaptureRepository();
        var now = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var handler = BuildHandler(repo, new FixedDateTimeProvider(now));

        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-2)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Rejected, now.AddHours(-1), "Parser failed"));

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Equal(1, result.TotalAccepted);
        Assert.Equal(1, result.TotalRejected);
        Assert.NotNull(result.LastErrorAtUtc);
        Assert.Equal("Parser failed", result.LastErrorMessage);
        Assert.Equal("Review rejected event error: Parser failed", result.RecoveryHint);
    }

    [Fact]
    public async Task HandleAsync_RejectedWithValidationError_ReturnsValidationRecoveryHint()
    {
        var repo = new FakeNotificationCaptureRepository();
        var now = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var handler = BuildHandler(repo, new FixedDateTimeProvider(now));

        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-2)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Rejected, now.AddHours(-1), "Field validation failed: amount"));

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.Equal("Check notification format settings", result.RecoveryHint);
    }

    [Fact]
    public async Task HandleAsync_NoRecentEvents_ReturnsInactiveWithPermissionHint()
    {
        var repo = new FakeNotificationCaptureRepository();
        var now = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var handler = BuildHandler(repo, new FixedDateTimeProvider(now));

        // Event from 10 days ago
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddDays(-10)));

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.False(result.IsActive);
        Assert.Equal("Verify notification access permission is enabled", result.RecoveryHint);
    }

    [Fact]
    public async Task HandleAsync_MixedStatuses_ReturnsCorrectCounts()
    {
        var repo = new FakeNotificationCaptureRepository();
        var now = new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);
        var handler = BuildHandler(repo, new FixedDateTimeProvider(now));

        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-3)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Accepted, now.AddHours(-2)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Duplicate, now.AddHours(-1)));
        repo.IngestEvents.Add(CreateEvent(IngestStatus.Rejected, now.AddMinutes(-30), "Some error"));

        var result = await handler.HandleAsync(new GetIntegrationStatusQuery(CoupleId), CancellationToken.None);

        Assert.True(result.IsActive);
        Assert.Equal(2, result.TotalAccepted);
        Assert.Equal(1, result.TotalDuplicate);
        Assert.Equal(1, result.TotalRejected);
        Assert.Equal("Some error", result.LastErrorMessage);
    }
}

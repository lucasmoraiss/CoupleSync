using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.Notifications;

public sealed class NotificationDispatcherJobTests
{
    private static readonly DateTime FixedNow = new(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly FixedDateTimeProvider DateTimeProvider = new(FixedNow);

    private static ServiceProvider BuildServiceProvider(
        FakeNotificationEventRepository eventRepo,
        FakeDeviceTokenRepository tokenRepo)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationEventRepository>(eventRepo);
        services.AddSingleton<IDeviceTokenRepository>(tokenRepo);
        services.AddSingleton<IDateTimeProvider>(DateTimeProvider);
        return services.BuildServiceProvider();
    }

    private static NotificationEvent BuildPendingEvent(Guid coupleId, Guid userId)
    {
        return NotificationEvent.Create(
            coupleId: coupleId,
            userId: userId,
            alertType: "LargeTransaction",
            title: "Large Transaction",
            body: "R$ 600 deducted",
            nowUtc: FixedNow);
    }

    private static DeviceToken BuildDeviceToken(Guid userId, Guid coupleId)
    {
        return DeviceToken.Create(userId, coupleId, "fcm-token-abc123", FixedNow);
    }

    [Fact]
    public async Task ExecuteAsync_PendingEvent_SuccessfulSend_MarksDelivered()
    {
        // Arrange
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var eventRepo = new FakeNotificationEventRepository();
        var tokenRepo = new FakeDeviceTokenRepository();

        var notificationEvent = BuildPendingEvent(coupleId, userId);
        eventRepo.Events.Add(notificationEvent);
        tokenRepo.Add(BuildDeviceToken(userId, coupleId));

        var stubFcm = new StubFcmAdapter(returnsSuccess: true);
        var serviceProvider = BuildServiceProvider(eventRepo, tokenRepo);

        var job = new NotificationDispatcherJob(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();

        // Act — run one iteration by starting and immediately cancelling
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100); // let worker start
        cts.Cancel();
        await runTask;

        // Assert
        Assert.Equal("Delivered", notificationEvent.Status);
        Assert.NotNull(notificationEvent.DeliveredAtUtc);
    }

    [Fact]
    public async Task ExecuteAsync_PendingEvent_FailedSend_MarksEventFailed()
    {
        // Arrange
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var eventRepo = new FakeNotificationEventRepository();
        var tokenRepo = new FakeDeviceTokenRepository();

        var notificationEvent = BuildPendingEvent(coupleId, userId);
        eventRepo.Events.Add(notificationEvent);
        tokenRepo.Add(BuildDeviceToken(userId, coupleId));

        var stubFcm = new StubFcmAdapter(returnsSuccess: false);
        var serviceProvider = BuildServiceProvider(eventRepo, tokenRepo);

        var job = new NotificationDispatcherJob(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Assert
        Assert.Equal("Failed", notificationEvent.Status);
    }

    [Fact]
    public async Task ExecuteAsync_PendingEvent_NoDeviceTokens_EventMarkedFailed()
    {
        // Arrange
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var eventRepo = new FakeNotificationEventRepository();
        var tokenRepo = new FakeDeviceTokenRepository(); // no tokens registered for userId

        var notificationEvent = BuildPendingEvent(coupleId, userId);
        eventRepo.Events.Add(notificationEvent);

        var stubFcm = new StubFcmAdapter(returnsSuccess: true);
        var serviceProvider = BuildServiceProvider(eventRepo, tokenRepo);

        var job = new NotificationDispatcherJob(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Assert — FCM was never called
        Assert.Equal(0, stubFcm.SendCallCount);
        // Assert — event was marked Failed (not left as Pending)
        Assert.Equal("Failed", notificationEvent.Status);
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingEvents_NoFcmCallsMade()
    {
        // Arrange
        var eventRepo = new FakeNotificationEventRepository(); // empty
        var tokenRepo = new FakeDeviceTokenRepository();

        var stubFcm = new StubFcmAdapter(returnsSuccess: true);
        var serviceProvider = BuildServiceProvider(eventRepo, tokenRepo);

        var job = new NotificationDispatcherJob(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();

        // Act
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        // Assert
        Assert.Equal(0, stubFcm.SendCallCount);
    }

    private sealed class StubFcmAdapter : IFcmAdapter
    {
        private readonly bool _returnsSuccess;
        public int SendCallCount { get; private set; }

        public StubFcmAdapter(bool returnsSuccess)
        {
            _returnsSuccess = returnsSuccess;
        }

        public Task<bool> SendAsync(string deviceToken, string title, string body, CancellationToken ct)
        {
            SendCallCount++;
            return Task.FromResult(_returnsSuccess);
        }
    }
}

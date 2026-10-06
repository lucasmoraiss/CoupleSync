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
        FakeDeviceTokenRepository tokenRepo,
        FakeCoupleRepository? couples = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INotificationEventRepository>(eventRepo);
        services.AddSingleton<IDeviceTokenRepository>(tokenRepo);
        services.AddSingleton<IDateTimeProvider>(DateTimeProvider);
        services.AddSingleton<ICoupleMembership>(couples is null ? new EveryoneIsAMember() : new MembersOf(couples));
        services.AddSingleton<ICoupleRepository>(couples ?? new FakeCoupleRepository());
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

    [Fact]
    public async Task ExecuteAsync_EventOfAGroupTheUserHasLeft_IsDroppedWithoutSending()
    {
        var user = User.Create(CoupleSync.Domain.ValueObjects.EmailAddress.From("ana@example.com"), "Ana", "hash", FixedNow);
        var current = Couple.Create("CURRENT1", FixedNow);
        current.AddMember(user, FixedNow);
        var left = Couple.Create("LEFT1234", FixedNow);
        var couples = new FakeCoupleRepository();
        couples.Couples.AddRange([current, left]);

        var eventRepo = new FakeNotificationEventRepository();
        var tokenRepo = new FakeDeviceTokenRepository();
        var ofLeftGroup = BuildPendingEvent(left.Id, user.Id);
        var ofCurrentGroup = BuildPendingEvent(current.Id, user.Id);
        eventRepo.Events.AddRange([ofLeftGroup, ofCurrentGroup]);
        tokenRepo.Add(BuildDeviceToken(user.Id, current.Id));

        var stubFcm = new StubFcmAdapter(returnsSuccess: true);
        var job = new NotificationDispatcherJob(
            BuildServiceProvider(eventRepo, tokenRepo, couples).GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        Assert.Equal("Failed", ofLeftGroup.Status);
        Assert.Equal("Delivered", ofCurrentGroup.Status);
        Assert.Equal(1, stubFcm.SendCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_UserInSeveralGroups_PushSaysWhichGroup_EvenWhenItIsNotTheActiveOne()
    {
        var ana = User.Create(CoupleSync.Domain.ValueObjects.EmailAddress.From("ana@example.com"), "Ana", "hash", FixedNow);
        var bruno = User.Create(CoupleSync.Domain.ValueObjects.EmailAddress.From("bruno@example.com"), "Bruno", "hash", FixedNow);
        var withBruno = Couple.Create("WITHBRUN", FixedNow);
        withBruno.AddMember(bruno, FixedNow);
        withBruno.AddMember(ana, FixedNow);
        var alone = Couple.Create("ALONE123", FixedNow);
        alone.AddMember(ana, FixedNow.AddDays(1)); // Ana's active group is "alone"; the alert is from the other one
        var couples = new FakeCoupleRepository();
        couples.Couples.AddRange([withBruno, alone]);

        var eventRepo = new FakeNotificationEventRepository();
        var tokenRepo = new FakeDeviceTokenRepository();
        var forAna = BuildPendingEvent(withBruno.Id, ana.Id);
        var forBruno = BuildPendingEvent(withBruno.Id, bruno.Id);
        eventRepo.Events.AddRange([forAna, forBruno]);
        tokenRepo.Add(DeviceToken.Create(ana.Id, alone.Id, "fcm-ana", FixedNow));
        tokenRepo.Add(DeviceToken.Create(bruno.Id, withBruno.Id, "fcm-bruno", FixedNow));

        var stubFcm = new StubFcmAdapter(returnsSuccess: true);
        var job = new NotificationDispatcherJob(
            BuildServiceProvider(eventRepo, tokenRepo, couples).GetRequiredService<IServiceScopeFactory>(),
            stubFcm,
            NullLogger<NotificationDispatcherJob>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = job.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await runTask;

        Assert.Equal("Delivered", forAna.Status);
        Assert.Equal("Large Transaction · Grupo com Bruno", stubFcm.TitlesByToken["fcm-ana"]);
        Assert.Equal("Large Transaction", stubFcm.TitlesByToken["fcm-bruno"]);
    }

    private sealed class MembersOf : ICoupleMembership
    {
        private readonly FakeCoupleRepository _couples;

        public MembersOf(FakeCoupleRepository couples) => _couples = couples;

        public Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
            => Task.FromResult(_couples.Couples.Any(c => c.Id == coupleId && c.HasMember(userId)));

        public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class EveryoneIsAMember : ICoupleMembership
    {
        public Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class StubFcmAdapter : IFcmAdapter
    {
        private readonly bool _returnsSuccess;
        public int SendCallCount { get; private set; }
        public Dictionary<string, string> TitlesByToken { get; } = new();

        public StubFcmAdapter(bool returnsSuccess)
        {
            _returnsSuccess = returnsSuccess;
        }

        public Task<bool> SendAsync(string deviceToken, string title, string body, CancellationToken ct)
        {
            SendCallCount++;
            TitlesByToken[deviceToken] = title;
            return Task.FromResult(_returnsSuccess);
        }
    }
}

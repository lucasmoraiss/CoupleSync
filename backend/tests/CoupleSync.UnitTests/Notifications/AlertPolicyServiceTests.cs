using CoupleSync.Application.Notification;
using CoupleSync.Domain.Entities;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.Notifications;

public sealed class AlertPolicyServiceTests
{
    private static readonly FixedDateTimeProvider Now =
        new(new DateTime(2026, 4, 16, 12, 0, 0, DateTimeKind.Utc));

    private static Transaction BuildTransaction(
        Guid coupleId, Guid userId, decimal amount, int daysAgo = 0)
    {
        return Transaction.Create(
            coupleId: coupleId,
            userId: userId,
            fingerprint: Guid.NewGuid().ToString("N"),
            bank: "NUBANK",
            amount: amount,
            currency: "BRL",
            eventTimestampUtc: Now.UtcNow.AddDays(-daysAgo),
            description: "Test",
            merchant: "Store",
            category: "OUTROS",
            ingestEventId: Guid.NewGuid(),
            createdAtUtc: Now.UtcNow);
    }

    private static NotificationSettings BuildSettings(
        Guid userId, Guid coupleId,
        bool lowBalance = true,
        bool largeTransaction = true,
        bool billReminder = true)
    {
        var settings = NotificationSettings.Create(userId, coupleId, Now.UtcNow);
        settings.Update(lowBalance, largeTransaction, billReminder, Now.UtcNow);
        return settings;
    }

    [Fact]
    public async Task EvaluatePostIngestAsync_AmountAbove500_CreatesLargeTransactionAlert()
    {
        var svc = new AlertPolicyService();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tx = BuildTransaction(coupleId, userId, 600m);
        var settings = BuildSettings(userId, coupleId);

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, [], settings, Now.UtcNow);

        Assert.Contains(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task EvaluatePostIngestAsync_AmountBelow500_DoesNotCreateLargeTransactionAlert()
    {
        var svc = new AlertPolicyService();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tx = BuildTransaction(coupleId, userId, 400m);
        var settings = BuildSettings(userId, coupleId);

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, [], settings, Now.UtcNow);

        Assert.DoesNotContain(events, e => e.AlertType == "LargeTransaction");
    }

    [Fact]
    public async Task EvaluatePostIngestAsync_RecentSpendAbove3000_CreatesLowBalanceAlert()
    {
        var svc = new AlertPolicyService();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tx = BuildTransaction(coupleId, userId, 50m);
        var recentTxs = new[]
        {
            BuildTransaction(coupleId, userId, 2000m, daysAgo: 5),
            BuildTransaction(coupleId, userId, 1500m, daysAgo: 10),
        };
        var settings = BuildSettings(userId, coupleId);

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, recentTxs, settings, Now.UtcNow);

        Assert.Contains(events, e => e.AlertType == "LowBalance");
    }

    [Fact]
    public async Task EvaluatePostIngestAsync_RecentSpendBelow3000_DoesNotCreateLowBalanceAlert()
    {
        var svc = new AlertPolicyService();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tx = BuildTransaction(coupleId, userId, 50m);
        var recentTxs = new[]
        {
            BuildTransaction(coupleId, userId, 500m, daysAgo: 5),
            BuildTransaction(coupleId, userId, 500m, daysAgo: 10),
        };
        var settings = BuildSettings(userId, coupleId);

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, recentTxs, settings, Now.UtcNow);

        Assert.DoesNotContain(events, e => e.AlertType == "LowBalance");
    }

    [Fact]
    public async Task EvaluatePostIngestAsync_LargeTransactionDisabledInSettings_NoAlertCreated()
    {
        var svc = new AlertPolicyService();
        var coupleId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var tx = BuildTransaction(coupleId, userId, 750m);
        var settings = BuildSettings(userId, coupleId, largeTransaction: false);

        var events = await svc.EvaluatePostIngestAsync(coupleId, userId, tx, [], settings, Now.UtcNow);

        Assert.DoesNotContain(events, e => e.AlertType == "LargeTransaction");
    }
}

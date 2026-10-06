using System.Text.Json;
using CoupleSync.Application.Notification;
using CoupleSync.Application.NotificationCapture;
using CoupleSync.Application.OcrImport;
using CoupleSync.Application.Transactions.Commands;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.OcrImport;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using FakeTransactionRepository = CoupleSync.UnitTests.Support.FakeTransactionRepository;

namespace CoupleSync.UnitTests.Notifications;

/// <summary>
/// A12 — GET /notifications/settings reports the three alerts as enabled by default, so a user
/// who never opened that screen (no row in notification_settings) must receive alerts as well.
/// </summary>
public sealed class AlertDefaultsWithoutSettingsTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedNow = new(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc);

    // Above AlertPolicyService's large-transaction threshold (500).
    private const decimal LargeAmount = 750m;

    [Fact]
    public async Task ManualTransaction_WithoutSettingsRow_GeneratesLargeTransactionAlert()
    {
        var events = new FakeNotificationEventRepository();
        var handler = BuildManualHandler(new FakeNotificationSettingsRepository(), events);

        await handler.HandleAsync(ManualCommand(), CancellationToken.None);

        var alert = Assert.Single(events.Events);
        Assert.Equal("LargeTransaction", alert.AlertType);
        Assert.Equal(UserId, alert.UserId);
        Assert.Equal(CoupleId, alert.CoupleId);
    }

    [Fact]
    public async Task ManualTransaction_WithoutSettingsRow_DoesNotCreateTheRow()
    {
        var settings = new FakeNotificationSettingsRepository();
        var handler = BuildManualHandler(settings, new FakeNotificationEventRepository());

        await handler.HandleAsync(ManualCommand(), CancellationToken.None);

        Assert.Null(await settings.GetByUserIdAsync(UserId, CoupleId, CancellationToken.None));
    }

    [Fact]
    public async Task ManualTransaction_WithAlertExplicitlyDisabled_GeneratesNoAlert()
    {
        var settings = new FakeNotificationSettingsRepository();
        await settings.UpsertAsync(UserId, CoupleId, lowBalance: false, largeTransaction: false, billReminder: false, FixedNow, CancellationToken.None);
        var events = new FakeNotificationEventRepository();
        var handler = BuildManualHandler(settings, events);

        await handler.HandleAsync(ManualCommand(), CancellationToken.None);

        Assert.Empty(events.Events);
    }

    [Fact]
    public async Task NotificationIngest_WithoutSettingsRow_GeneratesLargeTransactionAlert()
    {
        var events = new FakeNotificationEventRepository();
        var handler = new IngestNotificationEventCommandHandler(
            new FakeNotificationCaptureRepository(),
            new NotificationEventSanitizer(),
            new FixedDateTimeProvider(FixedNow),
            new FakeTransactionRepository(),
            new FakeCategoryMatchingService(),
            new FakeFingerprintGenerator(),
            Policy(new FakeNotificationSettingsRepository(), events),
            events);

        var result = await handler.HandleAsync(
            new IngestNotificationEventCommand(UserId, CoupleId, "NUBANK", LargeAmount, "BRL", FixedNow.AddHours(-1), "Compra", "Loja", null),
            CancellationToken.None);

        Assert.Equal("Accepted", result.Status);
        var alert = Assert.Single(events.Events);
        Assert.Equal("LargeTransaction", alert.AlertType);
    }

    [Fact]
    public async Task OcrConfirm_WithoutSettingsRow_GeneratesLargeTransactionAlert()
    {
        var events = new FakeNotificationEventRepository();
        var jobs = new FakeImportJobRepository();
        var service = new ImportJobService(
            jobs,
            new FakeStorageAdapter(),
            new FakeDateTimeProvider(FixedNow),
            new UniqueIndexTransactionRepository(),
            new FakeNotificationCaptureRepository(),
            Policy(new FakeNotificationSettingsRepository(), events),
            events,
            NullLogger<ImportJobService>.Instance);

        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", FixedNow);
        job.MarkProcessing(FixedNow);
        job.MarkReady(JsonSerializer.Serialize(new[]
        {
            new OcrCandidate
            {
                Index = 0, Date = FixedNow.Date, Description = "Passagem aérea", Amount = LargeAmount,
                Currency = "BRL", Confidence = 1.0, Fingerprint = "fp0000"
            }
        }), FixedNow);
        jobs.Jobs.Add(job);

        await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None);

        var alert = Assert.Single(events.Events);
        Assert.Equal("LargeTransaction", alert.AlertType);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    // The author is the only member the fake couple repository knows about (no members registered).
    private static AlertPolicyService Policy(FakeNotificationSettingsRepository settings, FakeNotificationEventRepository events)
        => new(new FakeBudgetRepository(), new FakeTransactionRepository(), events, new FakeCoupleRepository(), settings);

    private static CreateManualTransactionCommand ManualCommand()
        => new(CoupleId, UserId, LargeAmount, "BRL", FixedNow.AddHours(-1), "Geladeira", "Loja", "Moradia");

    private static CreateManualTransactionCommandHandler BuildManualHandler(
        FakeNotificationSettingsRepository settings, FakeNotificationEventRepository events)
        => new(
            new FakeTransactionRepository(),
            new FakeNotificationCaptureRepository(),
            new FixedDateTimeProvider(FixedNow),
            Policy(settings, events),
            events,
            NullLogger<CreateManualTransactionCommandHandler>.Instance);
}

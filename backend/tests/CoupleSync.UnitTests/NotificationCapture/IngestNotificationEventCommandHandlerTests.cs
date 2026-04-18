using CoupleSync.Application.NotificationCapture;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Security;
using CoupleSync.UnitTests.Support;

namespace CoupleSync.UnitTests.NotificationCapture;

public sealed class IngestNotificationEventCommandHandlerTests
{
    private static IngestNotificationEventCommandHandler BuildHandler(
        FakeNotificationCaptureRepository? repo = null,
        FixedDateTimeProvider? dateTimeProvider = null,
        FakeTransactionRepository? transactionRepo = null,
        FakeCategoryMatchingService? categoryService = null)
    {
        return new IngestNotificationEventCommandHandler(
            repo ?? new FakeNotificationCaptureRepository(),
            new NotificationEventSanitizer(),
            dateTimeProvider ?? new FixedDateTimeProvider(new DateTime(2026, 4, 14, 10, 0, 0, DateTimeKind.Utc)),
            transactionRepo ?? new FakeTransactionRepository(),
            categoryService ?? new FakeCategoryMatchingService(),
            new FakeFingerprintGenerator(),
            new FakeAlertPolicyService(),
            new FakeNotificationEventRepository(),
            new FakeNotificationSettingsRepository());
    }

    private static IngestNotificationEventCommand BuildCommand(
        Guid? coupleId = null,
        string? description = null,
        string? merchant = null,
        string? rawNotificationText = null)
    {
        return new IngestNotificationEventCommand(
            UserId: Guid.NewGuid(),
            CoupleId: coupleId ?? Guid.NewGuid(),
            Bank: "NUBANK",
            Amount: 99.99m,
            Currency: "BRL",
            EventTimestamp: new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc),
            Description: description,
            Merchant: merchant,
            RawNotificationText: rawNotificationText);
    }

    [Fact]
    public async Task HandleAsync_SanitizesHtmlFromDescription_BeforePersistence()
    {
        var repo = new FakeNotificationCaptureRepository();
        var handler = BuildHandler(repo);

        await handler.HandleAsync(BuildCommand(description: "<b>test</b>"), CancellationToken.None);

        var stored = Assert.Single(repo.IngestEvents);
        Assert.Equal("test", stored.Description);
    }

    [Fact]
    public async Task HandleAsync_TruncatesLongRawText_To512Chars()
    {
        var repo = new FakeNotificationCaptureRepository();
        var handler = BuildHandler(repo);
        var longText = new string('a', 600);

        await handler.HandleAsync(BuildCommand(rawNotificationText: longText), CancellationToken.None);

        var stored = Assert.Single(repo.IngestEvents);
        Assert.Equal(512, stored.RawNotificationTextRedacted!.Length);
    }

    [Fact]
    public async Task HandleAsync_SetsStatusToAccepted_ForValidPayload()
    {
        var repo = new FakeNotificationCaptureRepository();
        var handler = BuildHandler(repo);

        await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        var stored = Assert.Single(repo.IngestEvents);
        Assert.Equal(IngestStatus.Accepted, stored.Status);
    }

    [Fact]
    public async Task HandleAsync_SetsCreatedAtUtc_FromDateTimeProvider()
    {
        var expectedTime = new DateTime(2026, 4, 14, 12, 30, 0, DateTimeKind.Utc);
        var repo = new FakeNotificationCaptureRepository();
        var handler = BuildHandler(repo, new FixedDateTimeProvider(expectedTime));

        await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        var stored = Assert.Single(repo.IngestEvents);
        Assert.Equal(expectedTime, stored.CreatedAtUtc);
    }

    [Fact]
    public async Task HandleAsync_NewEvent_ReturnsAcceptedAndCreatesTransaction()
    {
        var repo = new FakeNotificationCaptureRepository();
        var transactionRepo = new FakeTransactionRepository();
        var categoryService = new FakeCategoryMatchingService();
        categoryService.AddRule("IFOOD", "Alimentação");
        var handler = BuildHandler(repo, transactionRepo: transactionRepo, categoryService: categoryService);

        var result = await handler.HandleAsync(BuildCommand(merchant: "IFOOD"), CancellationToken.None);

        Assert.Equal("Accepted", result.Status);
        Assert.Single(transactionRepo.Transactions);
        Assert.Equal("Alimentação", transactionRepo.Transactions[0].Category);
    }

    [Fact]
    public async Task HandleAsync_DuplicateFingerprint_ReturnsDuplicateAndNoTransaction()
    {
        var coupleId = Guid.NewGuid();
        var repo = new FakeNotificationCaptureRepository();
        var transactionRepo = new FakeTransactionRepository();
        var command = BuildCommand(coupleId: coupleId, merchant: "Test Store");

        // Pre-compute the fingerprint the handler will generate
        var fingerprint = TransactionFingerprintGenerator.GenerateStatic(
            coupleId, "NUBANK", 99.99m, "BRL",
            new DateTime(2026, 4, 14, 9, 0, 0, DateTimeKind.Utc), "Test Store");
        transactionRepo.AddExistingFingerprint(fingerprint, coupleId);

        var handler = BuildHandler(repo, transactionRepo: transactionRepo);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal("Duplicate", result.Status);
        Assert.Empty(transactionRepo.Transactions);
        Assert.Single(repo.IngestEvents);
        Assert.Equal(IngestStatus.Duplicate, repo.IngestEvents[0].Status);
    }

    [Fact]
    public async Task HandleAsync_NoKeywordMatch_AssignsOutrosCategory()
    {
        var transactionRepo = new FakeTransactionRepository();
        var categoryService = new FakeCategoryMatchingService(); // returns OUTROS by default
        var handler = BuildHandler(transactionRepo: transactionRepo, categoryService: categoryService);

        var result = await handler.HandleAsync(BuildCommand(merchant: "Unknown Place"), CancellationToken.None);

        Assert.Equal("Accepted", result.Status);
        Assert.Single(transactionRepo.Transactions);
        Assert.Equal("OUTROS", transactionRepo.Transactions[0].Category);
    }
}

using System.Text.Json;
using CoupleSync.Api.Contracts.Ocr;
using CoupleSync.Api.Validators;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// A04 (server side) — description/amount corrected by the user on the review screen
/// (<c>candidateEdits</c>) must be what gets stored.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class ImportJobCandidateEditsTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    // ── Service ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_WithEdits_StoresEditedDescriptionAndAmount()
    {
        var (service, job, transactions, ingests) = Build();
        var edits = new Dictionary<int, CandidateEdit>
        {
            [0] = new("Mercado do bairro", 150.00m),
            [1] = new(null, 25.90m),
            [2] = new("  Padaria da esquina ", null)
        };

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1, 2, 3], null, CancellationToken.None, edits);

        Assert.Equal(4, result!.Created.Count);
        var stored = transactions.Stored.OrderBy(t => t.Fingerprint).ToList();

        Assert.Equal("Mercado do bairro", stored[0].Description);
        Assert.Equal(150.00m, stored[0].Amount);

        Assert.Equal("Item 2", stored[1].Description);   // description untouched
        Assert.Equal(25.90m, stored[1].Amount);

        Assert.Equal("Padaria da esquina", stored[2].Description);
        Assert.Equal(30m, stored[2].Amount);              // amount untouched

        Assert.Equal("Item 4", stored[3].Description);   // line without edit
        Assert.Equal(40m, stored[3].Amount);

        // The audit trail (ingest event) carries the same values as the transaction.
        Assert.Contains(ingests.IngestEvents, i => i.Description == "Mercado do bairro" && i.Amount == 150.00m);
    }

    [Fact]
    public async Task Confirm_WithEdits_KeepsTheOriginalFingerprint_SoReimportIsStillDetected()
    {
        var (service, job, transactions, _) = Build();
        var edits = new Dictionary<int, CandidateEdit> { [0] = new("Mercado do bairro", 150.00m) };

        await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None, edits);

        var stored = Assert.Single(transactions.Stored);
        Assert.Equal("fp0000", stored.Fingerprint);
        Assert.True(await transactions.FingerprintExistsAsync("fp0000", CoupleId, CancellationToken.None));
    }

    [Theory]
    [InlineData(99)]   // does not exist in the import
    [InlineData(3)]    // exists but was not selected
    public async Task Confirm_EditForIndexNotSelected_Throws422_AndLeavesTheJobReady(int editedIndex)
    {
        var (service, job, transactions, _) = Build();
        var edits = new Dictionary<int, CandidateEdit> { [editedIndex] = new("Qualquer", 10m) };

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None, edits));

        Assert.Equal("INVALID_SELECTION", ex.Code);
        Assert.Equal(ImportJobStatus.Ready, job.Status);
        Assert.Empty(transactions.Stored);
    }

    // ── Request validator ──────────────────────────────────────────────────

    private static bool IsValid(params OcrCandidateEdit[] edits)
        => new ConfirmRequestValidator().Validate(new ConfirmRequest([0, 1], null, edits)).IsValid;

    [Fact]
    public void Validator_AcceptsWellFormedEdits()
    {
        Assert.True(IsValid(new OcrCandidateEdit(0, "Mercado", 150m), new OcrCandidateEdit(1, null, 25.9m)));
        Assert.True(IsValid(new OcrCandidateEdit(0, new string('d', 512), MoneyRules.MaxAmount)));
        Assert.True(new ConfirmRequestValidator().Validate(new ConfirmRequest([0, 1])).IsValid);
    }

    [Fact]
    public void Validator_RejectsRepeatedIndex()
        => Assert.False(IsValid(new OcrCandidateEdit(0, "A", null), new OcrCandidateEdit(0, null, 10m)));

    [Fact]
    public void Validator_RejectsEditForIndexThatIsNotSelected()
        => Assert.False(IsValid(new OcrCandidateEdit(7, "A", null)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validator_RejectsBlankDescription(string description)
        => Assert.False(IsValid(new OcrCandidateEdit(0, description, null)));

    [Fact]
    public void Validator_RejectsDescriptionLongerThan512()
        => Assert.False(IsValid(new OcrCandidateEdit(0, new string('d', 513), null)));

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("10.005")]
    [InlineData("0.001")]
    [InlineData("1000000000000")]
    public void Validator_RejectsInvalidAmount(string amount)
        => Assert.False(IsValid(new OcrCandidateEdit(0, null, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))));

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (ImportJobService, ImportJob, UniqueIndexTransactionRepository, FakeNotificationCaptureRepository) Build()
    {
        var jobs = new FakeImportJobRepository();
        var transactions = new UniqueIndexTransactionRepository();
        var ingests = new FakeNotificationCaptureRepository();
        var service = new ImportJobService(
            jobs,
            new FakeStorageAdapter(),
            new FakeDateTimeProvider(FixedNow),
            transactions,
            ingests,
            new FakeAlertPolicyService(),
            new FakeNotificationEventRepository(),
            new FakeNotificationSettingsRepository(),
            NullLogger<ImportJobService>.Instance);

        var candidates = Enumerable.Range(0, 4)
            .Select(i => new OcrCandidate
            {
                Index = i,
                Date = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc),
                Description = $"Item {i + 1}",
                Amount = 10m * (i + 1),
                Currency = "BRL",
                Confidence = 1.0,
                Fingerprint = $"fp{i:D4}"
            })
            .ToList();

        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", FixedNow);
        job.MarkProcessing(FixedNow);
        job.MarkReady(JsonSerializer.Serialize(candidates), FixedNow);
        jobs.Jobs.Add(job);

        return (service, job, transactions, ingests);
    }
}

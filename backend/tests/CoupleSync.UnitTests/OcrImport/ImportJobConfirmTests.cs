using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// A02 + A14 — confirming a statement import must cope with repeated lines, with lines that
/// were already imported, with invalid selections and with concurrent confirmations.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class ImportJobConfirmTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StatementDay = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    // ── A14: fingerprint of repeated statement lines ───────────────────────

    [Fact]
    public async Task ParseAndDeduplicate_IdenticalLinesOnTheSameDay_GetDistinctFingerprints()
    {
        var service = new OcrProcessingService(new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository());

        var candidates = await service.ParseAndDeduplicateAsync(CoupleId, StatementWithTwoIdenticalRides(), CancellationToken.None);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(3, candidates.Select(c => c.Fingerprint).Distinct().Count());
        Assert.All(candidates, c => Assert.False(c.DuplicateSuspected));
    }

    [Fact]
    public async Task ParseAndDeduplicate_SameStatementTwice_ProducesTheSameFingerprints()
    {
        var service = new OcrProcessingService(new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository());

        var first = await service.ParseAndDeduplicateAsync(CoupleId, StatementWithTwoIdenticalRides(), CancellationToken.None);
        var second = await service.ParseAndDeduplicateAsync(CoupleId, StatementWithTwoIdenticalRides(), CancellationToken.None);

        Assert.Equal(first.Select(c => c.Fingerprint), second.Select(c => c.Fingerprint));
    }

    [Fact]
    public async Task ParseAndDeduplicate_FirstOccurrenceKeepsTheLegacyFingerprint()
    {
        // Transactions imported before the fix were stored with the fingerprint without ordinal;
        // re-importing the same statement must still recognise them.
        var service = new OcrProcessingService(new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository());

        var candidates = await service.ParseAndDeduplicateAsync(CoupleId, StatementWithTwoIdenticalRides(), CancellationToken.None);

        var legacy = OcrProcessingService.ComputeFingerprint(CoupleId, StatementDay, 12.90m, "Corrida App");
        Assert.Equal(legacy, candidates[0].Fingerprint);
        Assert.NotEqual(legacy, candidates[1].Fingerprint);
        Assert.Equal(OcrProcessingService.ComputeFingerprint(CoupleId, StatementDay, 12.90m, "Corrida App", occurrence: 2), candidates[1].Fingerprint);
    }

    // ── A02 (a): identical lines inside the same statement ─────────────────

    [Fact]
    public async Task Confirm_StatementWithTwoIdenticalLines_StoresBoth()
    {
        var (service, jobs, transactions) = BuildService();
        var processing = new OcrProcessingService(transactions, new NullCategoryClassifier(), new FakeBudgetRepository());
        var candidates = await processing.ParseAndDeduplicateAsync(CoupleId, StatementWithTwoIdenticalRides(), CancellationToken.None);
        var job = AddReadyJob(jobs, candidates);

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1, 2], null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.Created.Count);
        Assert.Equal(0, result.DuplicatesSkipped);
        Assert.Equal(2, transactions.Stored.Count(t => t.Description == "Corrida App" && t.Amount == 12.90m));
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
    }

    [Fact]
    public async Task Confirm_JobProcessedBeforeTheFix_WithRepeatedFingerprint_StillStoresBothLines()
    {
        // A job that was already Ready when the fix was deployed carries the same fingerprint twice.
        var (service, jobs, transactions) = BuildService();
        var legacy = OcrProcessingService.ComputeFingerprint(CoupleId, StatementDay, 12.90m, "Corrida App");
        var job = AddReadyJob(jobs,
        [
            Candidate(0, "Corrida App", 12.90m, legacy),
            Candidate(1, "Corrida App", 12.90m, legacy)
        ]);

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None);

        Assert.Equal(2, result!.Created.Count);
        Assert.Equal(2, transactions.Stored.Select(t => t.Fingerprint).Distinct().Count());
        Assert.Equal(legacy, transactions.Stored[0].Fingerprint);
    }

    // ── A02 (b): lines that already exist in the database ──────────────────

    [Fact]
    public async Task Confirm_LinesAlreadyImported_AreSkippedAndReported()
    {
        var (service, jobs, transactions) = BuildService();
        transactions.SeedExisting(CoupleId, "fp0000");
        var job = AddReadyJob(jobs,
        [
            Candidate(0, "Mercado", 80m, "fp0000"),
            Candidate(1, "Farmácia", 35m, "fp0001")
        ]);

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None);

        Assert.Single(result!.Created);
        Assert.Equal("Farmácia", result.Created[0].Description);
        Assert.Equal(1, result.DuplicatesSkipped);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
    }

    [Fact]
    public async Task Confirm_ReimportedStatementWhereEverythingIsDuplicate_CreatesNothingAndClosesTheJob()
    {
        var (service, jobs, transactions) = BuildService();
        transactions.SeedExisting(CoupleId, "fp0000");
        transactions.SeedExisting(CoupleId, "fp0001");
        var job = AddReadyJob(jobs,
        [
            Candidate(0, "Mercado", 80m, "fp0000"),
            Candidate(1, "Farmácia", 35m, "fp0001")
        ]);

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None);

        Assert.Empty(result!.Created);
        Assert.Equal(2, result.DuplicatesSkipped);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
        Assert.Empty(transactions.Stored);
    }

    // ── A02 (c): selection that does not exist ─────────────────────────────

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Confirm_IndexThatDoesNotExist_Throws422_AndLeavesTheJobReady(int badIndex)
    {
        var (service, jobs, transactions) = BuildService();
        var job = AddReadyJob(jobs,
        [
            Candidate(0, "Mercado", 80m, "fp0000"),
            Candidate(1, "Farmácia", 35m, "fp0001")
        ]);

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, badIndex], null, CancellationToken.None));

        Assert.Equal("INVALID_SELECTION", ex.Code);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal(ImportJobStatus.Ready, job.Status);
        Assert.Empty(transactions.Stored);
    }

    [Fact]
    public async Task Confirm_RepeatedSelectedIndex_IsStoredOnlyOnce()
    {
        var (service, jobs, transactions) = BuildService();
        var job = AddReadyJob(jobs, [Candidate(0, "Mercado", 80m, "fp0000")]);

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 0], null, CancellationToken.None);

        Assert.Single(result!.Created);
        Assert.Single(transactions.Stored);
    }

    // ── A02 (d): concurrent confirmation ───────────────────────────────────

    [Fact]
    public async Task Confirm_WhenAnotherRequestStoredTheSameLinesFirst_Throws409()
    {
        var (service, jobs, transactions) = BuildService();
        var job = AddReadyJob(jobs, [Candidate(0, "Mercado", 80m, "fp0000")]);
        // The competing request commits between our duplicate check and our INSERT.
        transactions.BeforeSave = () => transactions.SeedExisting(CoupleId, "fp0000");

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None));

        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("OCR_CONFIRM_CONFLICT", ex.Code);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (ImportJobService Service, FakeImportJobRepository Jobs, UniqueIndexTransactionRepository Transactions) BuildService()
    {
        var jobs = new FakeImportJobRepository();
        var transactions = new UniqueIndexTransactionRepository();
        var service = new ImportJobService(
            jobs,
            new FakeStorageAdapter(),
            new FakeDateTimeProvider(FixedNow),
            transactions,
            new FakeNotificationCaptureRepository(),
            new FakeAlertPolicyService(),
            new FakeNotificationEventRepository(),
            new FakeNotificationSettingsRepository(),
            NullLogger<ImportJobService>.Instance);
        return (service, jobs, transactions);
    }

    private static ImportJob AddReadyJob(FakeImportJobRepository jobs, IReadOnlyList<OcrCandidate> candidates)
    {
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", FixedNow);
        job.MarkProcessing(FixedNow);
        job.MarkReady(JsonSerializer.Serialize(candidates), FixedNow);
        jobs.Jobs.Add(job);
        return job;
    }

    private static OcrCandidate Candidate(int index, string description, decimal amount, string fingerprint)
        => new()
        {
            Index = index,
            Date = StatementDay,
            Description = description,
            Amount = amount,
            Currency = "BRL",
            Confidence = 1.0,
            Fingerprint = fingerprint
        };

    /// <summary>Statement with two legitimate, identical rides on the same day plus one other purchase.</summary>
    private static string StatementWithTwoIdenticalRides()
        => JsonSerializer.Serialize(new
        {
            provider = "local-pdf",
            transactions = new[]
            {
                new { date = "2026-01-10", description = "Corrida App", amount = 12.90m, type = "Debit" },
                new { date = "2026-01-10", description = "Corrida App", amount = 12.90m, type = "Debit" },
                new { date = "2026-01-10", description = "Padaria", amount = 18.50m, type = "Debit" }
            }
        });
}

/// <summary>
/// Transaction repository fake that behaves like the real table: it has a unique index on
/// (couple_id, fingerprint) and SaveChanges fails as EF Core does when the index is violated.
/// </summary>
internal sealed class UniqueIndexTransactionRepository : ITransactionRepository
{
    private readonly HashSet<string> _committed = new();
    private readonly List<Transaction> _pending = new();

    public List<Transaction> Stored { get; } = new();

    public Action? BeforeSave { get; set; }

    public void SeedExisting(Guid coupleId, string fingerprint) => _committed.Add(Key(coupleId, fingerprint));

    public Task<bool> FingerprintExistsAsync(string fingerprint, Guid coupleId, CancellationToken ct)
        => Task.FromResult(_committed.Contains(Key(coupleId, fingerprint)));

    public Task AddTransactionAsync(Transaction transaction, CancellationToken ct)
    {
        _pending.Add(transaction);
        return Task.CompletedTask;
    }

    public Task AddTransactionsRangeAsync(IEnumerable<Transaction> transactions, CancellationToken ct)
    {
        _pending.AddRange(transactions);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        BeforeSave?.Invoke();

        var keys = new HashSet<string>(_committed);
        foreach (var transaction in _pending)
        {
            if (!keys.Add(Key(transaction.CoupleId, transaction.Fingerprint)))
            {
                _pending.Clear();
                throw new DbUpdateException(
                    "An error occurred while saving the entity changes.",
                    new InvalidOperationException("23505: duplicate key value violates unique constraint \"IX_transactions_couple_id_fingerprint\""));
            }
        }

        foreach (var transaction in _pending)
        {
            _committed.Add(Key(transaction.CoupleId, transaction.Fingerprint));
            Stored.Add(transaction);
        }

        _pending.Clear();
        return Task.CompletedTask;
    }

    public Task<(int TotalCount, IReadOnlyList<Transaction> Items)> GetPagedAsync(
        Guid coupleId, int page, int pageSize, string? category,
        DateTime? startDate, DateTime? endDate, CancellationToken ct)
        => Task.FromResult<(int, IReadOnlyList<Transaction>)>((Stored.Count, Stored));

    public Task<Transaction?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
        => Task.FromResult(Stored.FirstOrDefault(t => t.Id == id && t.CoupleId == coupleId));

    public Task<Transaction?> GetByIdRawAsync(Guid id, CancellationToken ct)
        => Task.FromResult(Stored.FirstOrDefault(t => t.Id == id));

    public Task DeleteAsync(Transaction transaction, CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<Transaction>> GetByGoalIdAsync(Guid goalId, Guid coupleId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>([]);

    public Task<IReadOnlyList<Transaction>> GetRecentByCoupleAsync(Guid coupleId, DateTime since, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Transaction>>(Stored);

    public Task UpdateAsync(Transaction transaction, CancellationToken ct = default) => Task.CompletedTask;

    public Task<Dictionary<string, decimal>> GetActualSpentByCategoryAsync(
        Guid coupleId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
        => Task.FromResult(new Dictionary<string, decimal>());

    private static string Key(Guid coupleId, string fingerprint) => $"{coupleId}|{fingerprint}";
}

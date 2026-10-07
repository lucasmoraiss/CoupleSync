using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// DAD-05 (job stuck in Processing), S5 5.33 (partial confirmation, per-line state) and
/// S5 5.58 (credits listed but never imported). All data below is invented.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class ImportJobLifecycleTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime Now = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime StatementDay = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    // ── DAD-05: stuck in Processing ────────────────────────────────────────

    [Fact]
    public async Task ReadingAJobStuckInProcessing_MarksItFailedWithAClearError()
    {
        var (service, jobs, _, storage) = Build();
        var job = AddJob(jobs, ImportJobStatus.Processing, updatedAt: Now.AddMinutes(-11));

        var read = await service.GetJobAsync(job.Id, CoupleId, CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, read!.Status);
        Assert.Equal("PROCESSING_TIMEOUT", read.ErrorCode);
        Assert.Contains("Envie o arquivo novamente", read.ErrorMessage);
        Assert.Equal(1, storage.Deletes);
    }

    [Fact]
    public async Task ReadingAJobThatIsStillWithinTheTimeout_LeavesItProcessing()
    {
        var (service, jobs, _, storage) = Build();
        var job = AddJob(jobs, ImportJobStatus.Processing, updatedAt: Now.AddMinutes(-9));

        var read = await service.GetJobAsync(job.Id, CoupleId, CancellationToken.None);

        Assert.Equal(ImportJobStatus.Processing, read!.Status);
        Assert.Equal(0, storage.Deletes);
    }

    [Theory]
    [InlineData(ImportJobStatus.Pending)]
    [InlineData(ImportJobStatus.Ready)]
    [InlineData(ImportJobStatus.Confirmed)]
    [InlineData(ImportJobStatus.Failed)]
    public async Task ReadingOldJobsInOtherStatuses_DoesNotChangeThem(ImportJobStatus status)
    {
        var (service, jobs, _, _) = Build();
        var job = AddJob(jobs, status, updatedAt: Now.AddDays(-3));

        var read = await service.GetJobAsync(job.Id, CoupleId, CancellationToken.None);

        Assert.Equal(status, read!.Status);
    }

    // ── S5 5.33: partial confirmation ──────────────────────────────────────

    [Fact]
    public async Task ConfirmingSomeLinesAndKeepingTheJobOpen_AllowsConfirmingTheRestLater()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m), Debit(2, "Padaria", 12m));

        var first = await service.ConfirmCandidatesAsync(
            job.Id, CoupleId, UserId, [0], null, CancellationToken.None, keepJobOpen: true);

        Assert.Single(first!.Created);
        Assert.Equal(2, first.RemainingLines);
        Assert.Equal(ImportJobStatus.Ready, job.Status);
        Assert.Equal(ImportLineState.Confirmed, job.GetLineState(0));
        Assert.Equal(ImportLineState.Pending, job.GetLineState(1));

        var second = await service.ConfirmCandidatesAsync(
            job.Id, CoupleId, UserId, [1, 2], null, CancellationToken.None, keepJobOpen: true);

        Assert.Equal(2, second!.Created.Count);
        Assert.Equal(0, second.RemainingLines);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
        Assert.Equal(3, transactions.Stored.Count);
    }

    [Fact]
    public async Task ConfirmingTheSameLineTwice_NeverDuplicatesIt()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m));

        await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None, keepJobOpen: true);
        var again = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None, keepJobOpen: true);

        Assert.Single(again!.Created);
        Assert.Equal("Farmácia", again.Created[0].Description);
        Assert.Equal(1, again.DuplicatesSkipped);
        Assert.Equal(2, transactions.Stored.Count);
    }

    [Fact]
    public async Task ConfirmWithoutKeepJobOpen_ClosesTheJobAndDropsTheUnselectedLines()
    {
        // This is the call the installed app makes: select, confirm, expect the job to finish.
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m));

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None);

        Assert.Single(result!.Created);
        Assert.Equal(0, result.RemainingLines);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
        Assert.Equal(ImportLineState.Discarded, job.GetLineState(1));
        Assert.Single(transactions.Stored);
    }

    [Fact]
    public async Task DiscardingTheLinesThatWereLeft_ClosesTheJob()
    {
        var (service, jobs, _, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m), Debit(2, "Padaria", 12m));
        await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None, keepJobOpen: true);

        var result = await service.ConfirmCandidatesAsync(
            job.Id, CoupleId, UserId, [], null, CancellationToken.None, keepJobOpen: true, discardedIndices: [1]);

        Assert.Empty(result!.Created);
        Assert.Equal(1, result.RemainingLines);
        Assert.Equal(ImportJobStatus.Ready, job.Status);

        var last = await service.ConfirmCandidatesAsync(
            job.Id, CoupleId, UserId, [], null, CancellationToken.None, keepJobOpen: true, discardedIndices: [2]);

        Assert.Equal(0, last!.RemainingLines);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
    }

    [Fact]
    public async Task ALineCannotBeImportedAndDiscardedInTheSameCall()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m));

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None, discardedIndices: [0]));

        Assert.Equal("INVALID_SELECTION", ex.Code);
        Assert.Empty(transactions.Stored);
    }

    [Fact]
    public void AJobWithoutLineStates_ReadsAsAllPending()
    {
        // Jobs created before the per-line state existed have no states stored.
        var (_, jobs, _, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m));

        Assert.Null(job.LineStatesJson);
        Assert.Equal(ImportLineState.Pending, job.GetLineState(0));
    }

    // ── S5 5.58: credits ───────────────────────────────────────────────────

    [Fact]
    public async Task Parsing_KeepsCreditsAfterTheDebits_WithoutShiftingDebitIndices()
    {
        var processing = new OcrProcessingService(new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository());
        var json = JsonSerializer.Serialize(new
        {
            provider = "local-pdf",
            transactions = new[]
            {
                new { date = "2026-01-05", description = "Reembolso Empresa Ficticia", amount = 500m, type = "Credit" },
                new { date = "2026-01-10", description = "Padaria Sol Nascente", amount = 18.50m, type = "Debit" },
                new { date = "2026-01-11", description = "Devolucao Loja Aurora", amount = 80m, type = "Credit" },
                new { date = "2026-01-12", description = "Mercado Horta Fresca", amount = 58.20m, type = "Debit" },
            }
        });

        var candidates = await processing.ParseAndDeduplicateAsync(CoupleId, json, CancellationToken.None);

        Assert.Equal(4, candidates.Count);
        Assert.Equal(["Padaria Sol Nascente", "Mercado Horta Fresca"], candidates.Take(2).Select(c => c.Description));
        Assert.Equal([0, 1, 2, 3], candidates.Select(c => c.Index));
        Assert.All(candidates.Skip(2), c => Assert.Equal(TransactionType.Credit, c.Type));
        Assert.All(candidates.Skip(2), c => Assert.Equal(string.Empty, c.Fingerprint));
    }

    [Fact]
    public async Task Review_SeparatesDebitLinesFromCredits()
    {
        var (service, jobs, _, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Credit(1, "Reembolso", 500m), Credit(2, "Estorno", 30m));

        var review = await service.GetReviewAsync(job.Id, CoupleId, CancellationToken.None);

        Assert.Single(review!.Lines);
        Assert.Equal(0, review.Lines[0].Candidate.Index);
        Assert.Equal(ImportLineState.Pending, review.Lines[0].State);
        Assert.Equal(2, review.Credits.Count);
    }

    [Fact]
    public async Task ConfirmingACreditIndex_IsRejected_AndNothingBecomesAnExpense()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Credit(1, "Reembolso", 500m));

        var ex = await Assert.ThrowsAsync<UnprocessableEntityException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0, 1], null, CancellationToken.None));

        Assert.Equal("INVALID_SELECTION", ex.Code);
        Assert.Empty(transactions.Stored);
        Assert.Equal(ImportJobStatus.Ready, job.Status);
    }

    [Fact]
    public async Task ConfirmingEveryDebit_ClosesTheJob_EvenWhenCreditsWereListed()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Credit(1, "Reembolso", 500m));

        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None);

        Assert.Single(result!.Created);
        Assert.Equal(0, result.RemainingLines);
        Assert.Equal(ImportJobStatus.Confirmed, job.Status);
        Assert.Single(transactions.Stored);
    }

    [Fact]
    public async Task AJobStoredBeforeCreditsWereKept_StillReadsAndConfirms()
    {
        // Old results have debit lines only and a null Type; they must keep working untouched.
        var (service, jobs, _, _) = Build();
        var legacy = new OcrCandidate { Index = 0, Date = StatementDay, Description = "Mercado", Amount = 80m, Currency = "BRL", Confidence = 1.0, Fingerprint = "fp-legacy" };
        var job = AddReadyJob(jobs, legacy);

        var review = await service.GetReviewAsync(job.Id, CoupleId, CancellationToken.None);
        var result = await service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None);

        Assert.Single(review!.Lines);
        Assert.Empty(review.Credits);
        Assert.Single(result!.Created);
    }

    // ── Fix round: concurrency, re-selecting, orphans, state guards ────────

    [Fact]
    public async Task ConcurrentConfirmationOfTheSameLine_LoserGetsTheConflict_AndNothingIsDuplicated()
    {
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m));
        // The competing request commits the same line between our duplicate check and our INSERT.
        transactions.BeforeSave = () => transactions.SeedExisting(CoupleId, "fp0000");

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [0], null, CancellationToken.None, keepJobOpen: true));

        Assert.Equal("OCR_CONFIRM_CONFLICT", ex.Code);
        Assert.Equal(409, ex.StatusCode);
        Assert.Empty(transactions.Stored);
    }

    [Fact]
    public async Task ConcurrentConfirmationOfDifferentLines_LoserGetsTheConflict_InsteadOfOverwritingTheLineStates()
    {
        // The job row carries a concurrency token: when another request changed it first, the write is
        // rejected (nothing of this request is stored) and the app is told to refresh and retry.
        var (service, jobs, transactions, _) = Build();
        var job = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m));
        transactions.BeforeSave = () => throw new CoupleSync.Application.Common.Exceptions.ConcurrencyConflictException("row changed by another request");

        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => service.ConfirmCandidatesAsync(job.Id, CoupleId, UserId, [1], null, CancellationToken.None, keepJobOpen: true));

        Assert.Equal("OCR_CONFIRM_CONFLICT", ex.Code);
        Assert.Empty(transactions.Stored);
    }

    [Fact]
    public async Task APreviouslyDiscardedLine_CanBeSelectedAgain_WhileTheJobIsOpen()
    {
        var (service, jobs, transactions, _) = Build();
        var open =AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m), Debit(2, "Padaria", 12m));
        await service.ConfirmCandidatesAsync(
            open.Id, CoupleId, UserId, [], null, CancellationToken.None, keepJobOpen: true, discardedIndices: [1]);
        Assert.Equal(ImportLineState.Discarded, open.GetLineState(1));
        Assert.Equal(ImportJobStatus.Ready, open.Status);

        var result = await service.ConfirmCandidatesAsync(
            open.Id, CoupleId, UserId, [1], null, CancellationToken.None, keepJobOpen: true);

        Assert.Single(result!.Created);
        Assert.Equal("Farmácia", result.Created[0].Description);
        Assert.Equal(ImportLineState.Confirmed, open.GetLineState(1));
        Assert.Equal(2, result.RemainingLines);
        Assert.Single(transactions.Stored.Where(t => t.Description == "Farmácia"));
    }

    [Fact]
    public async Task ApiStartup_FailsEveryJobInProcessing_EvenOnesFreshEnoughForTheOnReadRule()
    {
        // Single instance: whatever was Processing when the API starts belongs to a process that is gone.
        var jobs = new FakeImportJobRepository();
        var fresh = AddJob(jobs, ImportJobStatus.Processing, Now.AddSeconds(-20));
        var ready = AddJob(jobs, ImportJobStatus.Ready, Now.AddMinutes(-1));

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(jobs);
        services.AddSingleton<IStorageAdapter>(new CountingStorage());
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider(Now));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var recovered = await new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance)
            .RecoverStuckJobsAsync(CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(ImportJobStatus.Failed, fresh.Status);
        Assert.Equal("PROCESSING_TIMEOUT", fresh.ErrorCode);
        Assert.Equal(ImportJobStatus.Ready, ready.Status);
    }

    [Fact]
    public void AJobAlreadyFailedByRecovery_CannotBecomeReady_AndKeepsItsError()
    {
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", Now);
        job.MarkProcessing(Now);
        job.MarkFailed("PROCESSING_TIMEOUT", "interrompido", Now);

        Assert.Throws<InvalidOperationException>(() => job.MarkReady("[]", Now));

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PROCESSING_TIMEOUT", job.ErrorCode);
        Assert.Null(job.OcrResultJson);
    }

    [Fact]
    public void OnlyAReadyJobCanBeConfirmed()
    {
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", Now);
        job.MarkFailed("x", "y", Now);

        Assert.Throws<InvalidOperationException>(() => job.MarkConfirmed(Now));
    }

    [Fact]
    public async Task OpenImports_ListOnlyTheCouplesJobsThatStillHavePendingLines()
    {
        var (service, jobs, _, _) = Build();
        var partial = AddReadyJob(jobs, Debit(0, "Mercado", 80m), Debit(1, "Farmácia", 35m), Credit(2, "Reembolso", 50m));
        await service.ConfirmCandidatesAsync(partial.Id, CoupleId, UserId, [0], null, CancellationToken.None, keepJobOpen: true);
        var untouched = AddReadyJob(jobs, Debit(0, "Padaria", 12m));
        var finished = AddReadyJob(jobs, Debit(0, "Posto", 100m));
        await service.ConfirmCandidatesAsync(finished.Id, CoupleId, UserId, [0], null, CancellationToken.None);
        var otherCouple = ImportJob.Create(Guid.NewGuid(), UserId, "couples/x/z", "application/pdf", Now);
        otherCouple.MarkProcessing(Now);
        otherCouple.MarkReady(JsonSerializer.Serialize(new[] { Debit(0, "Alheio", 1m) }), Now);
        jobs.Jobs.Add(otherCouple);

        var open = await service.GetOpenImportsAsync(CoupleId, CancellationToken.None);

        Assert.Equal(2, open.Count);
        var partialItem = Assert.Single(open, i => i.UploadId == partial.Id);
        Assert.Equal((1, 2, 1), (partialItem.PendingLines, partialItem.TotalLines, partialItem.CreditsCount));
        Assert.Contains(open, i => i.UploadId == untouched.Id && i.PendingLines == 1);
        Assert.DoesNotContain(open, i => i.UploadId == otherCouple.Id);
    }

    [Fact]
    public async Task Upload_KeepsOnlyASafeFileName()
    {
        var (service, jobs, _, _) = Build();

        var id = await service.UploadAsync(
            CoupleId, UserId, new MemoryStream([1]), "application/pdf", CancellationToken.None, "C:\\Users\\x\\Extrato Setembro.pdf");

        Assert.Equal("Extrato Setembro.pdf", jobs.Jobs.Single(j => j.Id == id).SourceFileName);
    }

    [Fact]
    public async Task WhenRecoveryAlreadyFailedTheJob_TheWorkerDropsItsResult_AndDoesNotRetry()
    {
        var jobs = new ConcurrencyRaceRepository();
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", Now);
        jobs.Jobs.Add(job);
        var provider = new FixedProvider("""{"provider":"local-pdf","transactions":[{"date":"2026-01-10","description":"Padaria","amount":12.5,"type":"Debit"}]}""");

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(jobs);
        services.AddSingleton<IOcrProvider>(provider);
        services.AddSingleton<IStorageAdapter>(new CountingStorage());
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider(Now));
        services.AddSingleton(new OcrProcessingService(
            new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository()));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        await new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance)
            .ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(0, job.RetryCount);
        Assert.Equal(1, provider.Calls);
        Assert.True(jobs.ConflictRaised);
    }

    private sealed class FixedProvider(string json) : IOcrProvider
    {
        public int Calls { get; private set; }

        public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(json);
        }
    }

    /// <summary>Behaves like the real table when recovery failed the row first: saving "Ready" is a concurrency conflict.</summary>
    private sealed class ConcurrencyRaceRepository : IImportJobRepository
    {
        public List<ImportJob> Jobs { get; } = new();
        public bool ConflictRaised { get; private set; }

        public Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct)
            => Task.FromResult(Jobs.FirstOrDefault(j => j.Id == id && j.CoupleId == coupleId));

        public Task AddAsync(ImportJob job, CancellationToken ct) { Jobs.Add(job); return Task.CompletedTask; }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (Jobs.Any(j => j.Status == ImportJobStatus.Ready))
            {
                ConflictRaised = true;
                throw new CoupleSync.Application.Common.Exceptions.ConcurrencyConflictException("recovery failed the job first");
            }

            return Task.CompletedTask;
        }

        public Task ReloadAsync(ImportJob job, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportJob>>(Jobs.Where(j => j.Status == ImportJobStatus.Pending).ToList());

        public Task<IReadOnlyList<ImportJob>> GetReadyByCoupleAsync(Guid coupleId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportJob>>([]);

        public Task<IReadOnlyList<ImportJob>> GetStuckProcessingAsync(DateTime cutoffUtc, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportJob>>([]);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (ImportJobService Service, FakeImportJobRepository Jobs, UniqueIndexTransactionRepository Transactions, CountingStorage Storage) Build()
    {
        var jobs = new FakeImportJobRepository();
        var transactions = new UniqueIndexTransactionRepository();
        var storage = new CountingStorage();
        var service = new ImportJobService(
            jobs, storage, new FakeDateTimeProvider(Now), transactions,
            new FakeNotificationCaptureRepository(), new FakeAlertPolicyService(),
            new FakeNotificationEventRepository(), NullLogger<ImportJobService>.Instance);
        return (service, jobs, transactions, storage);
    }

    private static ImportJob AddJob(FakeImportJobRepository jobs, ImportJobStatus status, DateTime updatedAt)
    {
        var job = ImportJob.Create(CoupleId, UserId, $"couples/x/{Guid.NewGuid()}", "application/pdf", updatedAt);
        switch (status)
        {
            case ImportJobStatus.Processing: job.MarkProcessing(updatedAt); break;
            case ImportJobStatus.Ready: job.MarkProcessing(updatedAt); job.MarkReady("[]", updatedAt); break;
            case ImportJobStatus.Confirmed: job.MarkProcessing(updatedAt); job.MarkReady("[]", updatedAt); job.MarkConfirmed(updatedAt); break;
            case ImportJobStatus.Failed: job.MarkFailed("processing_error", "x", updatedAt); break;
        }

        jobs.Jobs.Add(job);
        return job;
    }

    private static ImportJob AddReadyJob(FakeImportJobRepository jobs, params OcrCandidate[] candidates)
    {
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/y", "application/pdf", Now);
        job.MarkProcessing(Now);
        job.MarkReady(JsonSerializer.Serialize(candidates), Now);
        jobs.Jobs.Add(job);
        return job;
    }

    private static OcrCandidate Debit(int index, string description, decimal amount)
        => new()
        {
            Index = index, Date = StatementDay, Description = description, Amount = amount, Currency = "BRL",
            Confidence = 1.0, Fingerprint = $"fp{index:0000}", Type = TransactionType.Debit
        };

    private static OcrCandidate Credit(int index, string description, decimal amount)
        => new()
        {
            Index = index, Date = StatementDay, Description = description, Amount = amount, Currency = "BRL",
            Confidence = 1.0, Type = TransactionType.Credit
        };

    private sealed class CountingStorage : IStorageAdapter
    {
        public int Deletes { get; private set; }

        public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
            => Task.FromResult($"couples/{coupleId}/{uploadId}");

        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string storagePath, CancellationToken ct)
        {
            Deletes++;
            return Task.CompletedTask;
        }
    }
}

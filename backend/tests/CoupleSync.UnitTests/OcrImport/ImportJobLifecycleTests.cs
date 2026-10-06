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

    [Fact]
    public async Task ApiStartup_FailsOnlyTheJobsStuckInProcessing()
    {
        var jobs = new FakeImportJobRepository();
        var stuck = AddJob(jobs, ImportJobStatus.Processing, Now.AddMinutes(-30));
        var running = AddJob(jobs, ImportJobStatus.Processing, Now.AddMinutes(-1));
        var ready = AddJob(jobs, ImportJobStatus.Ready, Now.AddHours(-5));

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(jobs);
        services.AddSingleton<IStorageAdapter>(new CountingStorage());
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider(Now));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var worker = new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance);

        var recovered = await worker.RecoverStuckJobsAsync(CancellationToken.None);

        Assert.Equal(1, recovered);
        Assert.Equal(ImportJobStatus.Failed, stuck.Status);
        Assert.Equal("PROCESSING_TIMEOUT", stuck.ErrorCode);
        Assert.Equal(ImportJobStatus.Processing, running.Status);
        Assert.Equal(ImportJobStatus.Ready, ready.Status);
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

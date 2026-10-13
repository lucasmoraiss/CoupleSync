using CoupleSync.Application.Ai;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.Storage;
using CoupleSync.Infrastructure.Persistence;
using CoupleSync.UnitTests.Ai;
using CoupleSync.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// A08 — a statement from an unsupported bank is a permanent failure: it must fail on the first
/// attempt with BANK_FORMAT_UNKNOWN instead of being retried and ending as processing_error.
/// </summary>
[Trait("Category", "Ocr")]
public sealed class OcrBackgroundJobTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UnknownBankFormat_FailsOnFirstAttempt_WithBankFormatUnknownCode()
    {
        var provider = new ThrowingOcrProvider(() => new BankFormatUnknownException());
        var (backgroundJob, jobs, storage) = Build(provider);
        var job = AddPendingJob(jobs);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("BANK_FORMAT_UNKNOWN", job.ErrorCode);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(1, storage.Deletes);

        // Nothing left to pick up: further polls do not touch the job again.
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task TransientFailure_IsAttemptedExactlyThreeTimes_ThenFailsAsProcessingError()
    {
        var provider = new ThrowingOcrProvider(() => new InvalidOperationException("storage temporarily unavailable"));
        var (backgroundJob, jobs, _) = Build(provider);
        var job = AddPendingJob(jobs);

        for (var poll = 0; poll < 10; poll++)
            await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("processing_error", job.ErrorCode);
        Assert.Equal(
            "Não conseguimos ler este extrato. Tente exportá-lo de novo no app ou no site do banco; se continuar, cadastre as transações manualmente.",
            job.ErrorMessage);
        Assert.Equal(3, provider.Calls);
    }

    // Issue 56: the disk of the instance does not survive a restart, so the file of a waiting job may be gone.

    [Fact]
    public async Task APendingJobWhoseFileIsGone_FailsAtOnceWithProcessingTimeout_InsteadOfBeingRetriedThreeTimes()
    {
        var provider = new ThrowingOcrProvider(() => new FileNotFoundException("Stored file not found at resolved path."));
        var (backgroundJob, jobs, _) = Build(provider);
        var job = AddPendingJob(jobs, ageMinutes: 3);

        for (var poll = 0; poll < 5; poll++)
            await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PROCESSING_TIMEOUT", job.ErrorCode);
        Assert.Equal(ImportJobRecovery.ProcessingTimeoutMessage, job.ErrorMessage);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task APendingJobWhoseFileIsMissing_WithinTheGracePeriod_StaysPendingWithoutSpendingAnAttempt_AndIsTriedAgain()
    {
        // During a deploy the other instance may own the file: this one must not fail the job.
        var provider = new ThrowingOcrProvider(() => new FileNotFoundException());
        var (backgroundJob, jobs, _) = Build(provider);
        var job = AddPendingJob(jobs, ageMinutes: 1);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(0, job.RetryCount);
        Assert.Null(job.ErrorCode);
        Assert.Null(job.ErrorMessage);
        Assert.Equal(1, provider.Calls);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Pending, job.Status);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task TheSameJob_OnceTheGracePeriodHasPassed_FailsWithProcessingTimeout()
    {
        var provider = new ThrowingOcrProvider(() => new FileNotFoundException());
        var clock = new MutableClock(FixedNow);
        var (backgroundJob, jobs, _) = Build(provider, clock: clock);
        var job = AddPendingJob(jobs);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);
        Assert.Equal(ImportJobStatus.Pending, job.Status);

        clock.UtcNow = FixedNow + OcrBackgroundJob.MissingFileGracePeriod;
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PROCESSING_TIMEOUT", job.ErrorCode);
        Assert.Equal(ImportJobRecovery.ProcessingTimeoutMessage, job.ErrorMessage);
        Assert.Equal(0, job.RetryCount);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task WhenRecoveryFailedTheJobFirst_TheGraceReturnToPendingIsDropped_TheVerdictStands_AndTheLoopGoesOn()
    {
        var provider = new PathAwareOcrProvider(missing: ["couples/x/missing.pdf"], json: ValidStatementJson);
        // Save #1 is the job going to Processing, save #2 is the worker handing it back to the queue: the row was
        // already changed by recovery, so that second write is a concurrency conflict.
        ConflictingRepository? repo = null;
        var (backgroundJob, jobs, _) = Build(provider, wrapRepository: inner => repo = new ConflictingRepository(inner, conflictOnSaveNumber: 2));
        var inGrace = AddPendingJob(jobs, ageMinutes: 1, storagePath: "couples/x/missing.pdf");
        var other = AddPendingJob(jobs, storagePath: "couples/x/present.pdf");

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(1, repo!.Reloads);
        Assert.Equal(ImportJobStatus.Failed, inGrace.Status);
        Assert.Equal("PROCESSING_TIMEOUT", inGrace.ErrorCode);
        Assert.Equal(ImportJobStatus.Ready, other.Status);
    }

    [Fact]
    public async Task WhenRecoveryFailedTheJobFirst_TheTimeoutWriteAfterTheGracePeriodIsDropped_TheVerdictStands_AndTheLoopGoesOn()
    {
        var provider = new PathAwareOcrProvider(missing: ["couples/x/missing.pdf"], json: ValidStatementJson);
        ConflictingRepository? repo = null;
        var (backgroundJob, jobs, _) = Build(provider, wrapRepository: inner => repo = new ConflictingRepository(inner, conflictOnSaveNumber: 2));
        var pastGrace = AddPendingJob(jobs, ageMinutes: 3, storagePath: "couples/x/missing.pdf");
        var other = AddPendingJob(jobs, storagePath: "couples/x/present.pdf");

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(1, repo!.Reloads);
        Assert.Equal(ImportJobStatus.Failed, pastGrace.Status);
        Assert.Equal("PROCESSING_TIMEOUT", pastGrace.ErrorCode);
        Assert.Equal(ImportJobStatus.Ready, other.Status);
    }

    [Fact]
    public async Task FiveJobsInTheGracePeriod_DoNotLeaveANewerJobUnread_AndEachIsReadOnlyOncePerPass()
    {
        var missing = Enumerable.Range(0, 5).Select(i => $"couples/x/missing-{i}.pdf").ToArray();
        var provider = new PathAwareOcrProvider(missing, ValidStatementJson);
        var clock = new MutableClock(FixedNow.AddSeconds(1));
        var (backgroundJob, jobs, _) = Build(provider, clock: clock);
        var waiting = missing.Select(path => AddPendingJob(jobs, ageMinutes: 1, storagePath: path)).ToList();
        var newer = AddPendingJob(jobs, storagePath: "couples/x/present.pdf");

        // Pass 1 takes the five oldest: all five are handed back, the newer job is outside the batch of five.
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.All(missing, path => Assert.Equal(1, provider.CallsFor(path)));
        Assert.Equal(0, provider.CallsFor("couples/x/present.pdf"));
        Assert.All(waiting, j => { Assert.Equal(ImportJobStatus.Pending, j.Status); Assert.Equal(0, j.RetryCount); });

        // Pass 2 (the next poll, 5 s later): the five just tried go behind the newer job, which takes the first slot.
        clock.UtcNow = FixedNow.AddSeconds(6);
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Ready, newer.Status);
        // The batch of five in pass 2 was the newer job plus the four first in line: each read once, none twice in one pass.
        Assert.Equal(1, provider.CallsFor("couples/x/present.pdf"));
        Assert.Equal(5 + 4, missing.Sum(provider.CallsFor));
        Assert.All(waiting, j => { Assert.Equal(ImportJobStatus.Pending, j.Status); Assert.Equal(0, j.RetryCount); });
    }

    [Fact]
    public async Task AJobThatArrivesWhileFiveAreInTheGracePeriod_IsReadByTheSecondPassAfterItArrives()
    {
        var missing = Enumerable.Range(0, 5).Select(i => $"couples/x/missing-{i}.pdf").ToArray();
        var provider = new PathAwareOcrProvider(missing, ValidStatementJson);
        var clock = new MutableClock(FixedNow);
        var (backgroundJob, jobs, _) = Build(provider, clock: clock);
        foreach (var path in missing) AddPendingJob(jobs, ageMinutes: 1, storagePath: path);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        var arrived = AddPendingJob(jobs, storagePath: "couples/x/present.pdf", createdAt: FixedNow.AddSeconds(2));
        clock.UtcNow = FixedNow.AddSeconds(5);
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);
        clock.UtcNow = FixedNow.AddSeconds(10);
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Ready, arrived.Status);
    }

    [Fact]
    public async Task TheRealLocalStorage_RaisesFileNotFound_ForAMissingFile_AndForAMissingGroupDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "couplesync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:BasePath"] = root })
                .Build();
            var storage = new LocalFileStorageAdapter(config, NullLogger<LocalFileStorageAdapter>.Instance);
            var coupleId = Guid.NewGuid();

            // Group directory does not exist at all.
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => storage.DownloadAsync($"uploads/{coupleId}/{Guid.NewGuid()}.pdf", CancellationToken.None));

            // Group directory exists, file does not.
            Directory.CreateDirectory(Path.Combine(root, coupleId.ToString()));
            await Assert.ThrowsAsync<FileNotFoundException>(
                () => storage.DownloadAsync($"uploads/{coupleId}/{Guid.NewGuid()}.pdf", CancellationToken.None));

            // Control: a file that is there is read, so the exceptions above come from the file being absent.
            var present = Guid.NewGuid();
            await File.WriteAllBytesAsync(Path.Combine(root, coupleId.ToString(), $"{present}.pdf"), [1, 2, 3]);
            await using var stream = await storage.DownloadAsync($"uploads/{coupleId}/{present}.pdf", CancellationToken.None);
            Assert.Equal(3, stream.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterARestart_NoJobStaysPendingOrProcessing_EveryOneReachesAnEndWithTheRightCode()
    {
        var provider = new ThrowingOcrProvider(() => new FileNotFoundException());
        var (backgroundJob, jobs, _) = Build(provider);
        var queued = AddPendingJob(jobs, ageMinutes: 3);
        var interrupted = AddPendingJob(jobs, ageMinutes: 3);
        interrupted.MarkProcessing(FixedNow);

        // What the worker does when it starts: recover the Processing ones, then poll the queue.
        await backgroundJob.RecoverStuckJobsAsync(CancellationToken.None);
        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.All(new[] { queued, interrupted }, j =>
        {
            Assert.Equal(ImportJobStatus.Failed, j.Status);
            Assert.Equal("PROCESSING_TIMEOUT", j.ErrorCode);
        });
    }

    [Fact]
    public async Task OcrException_StillFailsImmediatelyWithItsOwnCode()
    {
        var provider = new ThrowingOcrProvider(() => new OcrException("PDF_ENCRYPTED", "PDF protegido por senha."));
        var (backgroundJob, jobs, _) = Build(provider);
        var job = AddPendingJob(jobs);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PDF_ENCRYPTED", job.ErrorCode);
        Assert.Equal(1, provider.Calls);
    }

    // ── Issue #38: descriptions only go to the classifier while the group has the AI switched on ──

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    public async Task TheClassifier_OnlySeesTheStatement_WhenTheUploaderAskedAndTheGroupHasTheAiOnAtTheServer(
        bool uploaderAsked, bool groupEnabled, int expectedDescriptions)
    {
        var classifier = new SpyClassifier();
        var gate = new FixedConsentGate(groupEnabled);
        var provider = new FixedOcrProvider(
            """{"provider":"local-pdf","transactions":[{"date":"2026-01-10","description":"Padaria Sol","amount":12.5,"type":"Debit"}]}""");
        var (backgroundJob, jobs, _) = Build(provider, classifier, gate);
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/statement.pdf", "application/pdf", FixedNow, null, aiCategorizationConsent: uploaderAsked);
        jobs.Jobs.Add(job);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Ready, job.Status);
        Assert.Equal(expectedDescriptions, classifier.Seen.Count);
        // The group is the one of the job, named explicitly: there is no token in background work.
        if (uploaderAsked) Assert.Equal([CoupleId], gate.Asked);
    }

    /// <summary>
    /// The import with the gate the API registers (ServerAiConsentGate), on a real database (SQLite): the statement
    /// of a group that never accepted anything goes to the classifier, and the one of a group that switched the AI
    /// off for the group does not.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task WithTheRealGate_AGroupWithNoAcceptanceHasTheStatementClassified_AndAGroupThatSwitchedTheAiOffDoesNot(
        bool switchedOff, int expectedDescriptions)
    {
        using var kit = new LlmGatewayTestKit();
        Guid groupId;
        using (var db = kit.NewContext())
        {
            var ana = User.Create(EmailAddress.From("ana@example.com"), "Ana Exemplo", "hash", FixedNow);
            var couple = Couple.Create("ABCD1234", FixedNow);
            couple.AddMember(ana, FixedNow);
            db.AddRange(ana, couple);
            db.SaveChanges();
            groupId = couple.Id;
            Assert.Empty(db.AiConsents.ToList());
            // "Off for the group", as the route stores it.
            if (switchedOff)
                Assert.Equal(1, db.Database.ExecuteSqlInterpolated($"UPDATE couples SET ai_disabled_at_utc = {FixedNow} WHERE id = {groupId}"));
        }

        var classifier = new SpyClassifier();
        var provider = new FixedOcrProvider(ValidStatementJson);
        var (backgroundJob, jobs, _) = Build(provider, classifier, new ServerAiConsentGate(new AiActivationRepository(kit.NewContext())));
        var job = ImportJob.Create(groupId, UserId, "couples/x/statement.pdf", "application/pdf", FixedNow, null, aiCategorizationConsent: true);
        jobs.Jobs.Add(job);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Ready, job.Status);
        Assert.Equal(expectedDescriptions, classifier.Seen.Count);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (OcrBackgroundJob Job, FakeImportJobRepository Jobs, CountingStorageAdapter Storage) Build(
        IOcrProvider provider, ICategoryClassifier? classifier = null, IAiConsentGate? gate = null, IDateTimeProvider? clock = null,
        Func<FakeImportJobRepository, IImportJobRepository>? wrapRepository = null)
    {
        var jobs = new FakeImportJobRepository();
        var storage = new CountingStorageAdapter();

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(wrapRepository is null ? jobs : wrapRepository(jobs));
        services.AddSingleton(provider);
        services.AddSingleton<IStorageAdapter>(storage);
        services.AddSingleton<IDateTimeProvider>(clock ?? new FakeDateTimeProvider(FixedNow));
        services.AddSingleton(new OcrProcessingService(
            new UniqueIndexTransactionRepository(), classifier ?? new NullCategoryClassifier(), new FakeBudgetRepository()));
        if (gate is not null) services.AddSingleton(gate);

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance), jobs, storage);
    }

    private static ImportJob AddPendingJob(
        FakeImportJobRepository jobs, int ageMinutes = 0, string storagePath = "couples/x/statement.pdf", DateTime? createdAt = null)
    {
        var job = ImportJob.Create(CoupleId, UserId, storagePath, "application/pdf", createdAt ?? FixedNow.AddMinutes(-ageMinutes));
        jobs.Jobs.Add(job);
        return job;
    }

    private const string ValidStatementJson =
        """{"provider":"local-pdf","transactions":[{"date":"2026-01-10","description":"Padaria Sol","amount":12.5,"type":"Debit"}]}""";

    /// <summary>Behaves like the real table when recovery changed the row first: that save is a concurrency conflict.</summary>
    private sealed class ConflictingRepository(FakeImportJobRepository inner, int conflictOnSaveNumber) : IImportJobRepository
    {
        private int _saves;

        public int Reloads { get; private set; }

        public Task SaveChangesAsync(CancellationToken ct)
        {
            if (++_saves == conflictOnSaveNumber)
                throw new ConcurrencyConflictException("recovery failed the job first");
            return inner.SaveChangesAsync(ct);
        }

        // The reload brings back what is stored: the verdict recovery wrote (same code and text as the worker's).
        public Task ReloadAsync(ImportJob job, CancellationToken ct)
        {
            Reloads++;
            job.MarkFailed(ImportJobRecovery.ProcessingTimeoutCode, ImportJobRecovery.ProcessingTimeoutMessage, FixedNow);
            return Task.CompletedTask;
        }

        public Task<ImportJob?> GetByIdAsync(Guid id, Guid coupleId, CancellationToken ct) => inner.GetByIdAsync(id, coupleId, ct);
        public Task AddAsync(ImportJob job, CancellationToken ct) => inner.AddAsync(job, ct);
        public Task<IReadOnlyList<ImportJob>> GetPendingAsync(int limit, CancellationToken ct) => inner.GetPendingAsync(limit, ct);
        public Task<IReadOnlyList<ImportJob>> GetReadyByCoupleAsync(Guid coupleId, int limit, CancellationToken ct) => inner.GetReadyByCoupleAsync(coupleId, limit, ct);
        public Task<IReadOnlyList<ImportJob>> GetStuckProcessingAsync(DateTime cutoffUtc, int limit, CancellationToken ct) => inner.GetStuckProcessingAsync(cutoffUtc, limit, ct);
        public Task<int> CountUnfinishedAsync(Guid coupleId, DateTime processingSinceUtc, CancellationToken ct) => inner.CountUnfinishedAsync(coupleId, processingSinceUtc, ct);
    }

    /// <summary>The files of the paths in <c>missing</c> are not on this instance's disk; the others are read normally.</summary>
    private sealed class PathAwareOcrProvider(IEnumerable<string> missing, string json) : IOcrProvider
    {
        private readonly HashSet<string> _missing = [.. missing];
        private readonly Dictionary<string, int> _calls = new();

        public int CallsFor(string path) => _calls.GetValueOrDefault(path);

        public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        {
            _calls[storagePath] = CallsFor(storagePath) + 1;
            if (_missing.Contains(storagePath)) throw new FileNotFoundException();
            return Task.FromResult(json);
        }
    }

    private sealed class MutableClock(DateTime now) : IDateTimeProvider
    {
        public DateTime UtcNow { get; set; } = now;
    }

    private sealed class FixedOcrProvider(string json) : IOcrProvider
    {
        public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct) => Task.FromResult(json);
    }

    private sealed class SpyClassifier : ICategoryClassifier
    {
        public List<string> Seen { get; } = new();

        public Task<string?> SuggestCategoryAsync(string description, IReadOnlyList<string> availableCategories, CancellationToken ct)
        {
            Seen.Add(description);
            return Task.FromResult<string?>("Alimentação");
        }
    }

    private sealed class FixedConsentGate(bool enabled) : IAiConsentGate
    {
        public List<Guid?> Asked { get; } = new();

        public Task<bool> IsEnabledAsync(Guid? coupleId, CancellationToken ct)
        {
            Asked.Add(coupleId);
            return Task.FromResult(enabled);
        }
    }

    private sealed class ThrowingOcrProvider : IOcrProvider
    {
        private readonly Func<Exception> _exceptionFactory;

        public ThrowingOcrProvider(Func<Exception> exceptionFactory) => _exceptionFactory = exceptionFactory;

        public int Calls { get; private set; }

        public Task<string> AnalyzeAsync(string storagePath, string mimeType, CancellationToken ct)
        {
            Calls++;
            throw _exceptionFactory();
        }
    }

    private sealed class CountingStorageAdapter : IStorageAdapter
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

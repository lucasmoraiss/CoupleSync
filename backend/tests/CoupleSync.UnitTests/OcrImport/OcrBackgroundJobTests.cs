using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.UnitTests.Support;
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
        Assert.Equal(3, provider.Calls);
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

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (OcrBackgroundJob Job, FakeImportJobRepository Jobs, CountingStorageAdapter Storage) Build(IOcrProvider provider)
    {
        var jobs = new FakeImportJobRepository();
        var storage = new CountingStorageAdapter();

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(jobs);
        services.AddSingleton(provider);
        services.AddSingleton<IStorageAdapter>(storage);
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider(FixedNow));
        services.AddSingleton(new OcrProcessingService(
            new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository()));

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance), jobs, storage);
    }

    private static ImportJob AddPendingJob(FakeImportJobRepository jobs)
    {
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/statement.pdf", "application/pdf", FixedNow);
        jobs.Jobs.Add(job);
        return job;
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

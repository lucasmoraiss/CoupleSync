using System.Diagnostics;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Infrastructure.BackgroundJobs;
using CoupleSync.Infrastructure.Integrations.Gemini;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser;
using CoupleSync.Infrastructure.Integrations.LocalPdfParser.Worker;
using CoupleSync.UnitTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoupleSync.UnitTests.OcrImport;

/// <summary>
/// Issue #14: when the PDF worker process dies in the middle of the read, the import job ends as a handled failure with
/// its own code: not retried as a generic error, not left in Processing, not an exception out of the background job.
/// </summary>
[Trait("Category", "PdfExtraction")]
public sealed class PdfWorkerImportJobTests
{
    private static readonly Guid CoupleId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTime FixedNow = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AWorkerKilledInTheMiddleOfTheRead_EndsTheJobAsFailedWithItsOwnCode_OnTheFirstAttempt()
    {
        var childId = 0;
        var extractor = new ChildProcessPdfTextExtractor(IdleWorker(id =>
        {
            childId = id;
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Process.GetProcessById(id).Kill(entireProcessTree: true);
            });
        }));
        var (backgroundJob, jobs) = Build(extractor);
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/statement.pdf", "application/pdf", FixedNow);
        jobs.Jobs.Add(job);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PDF_WORKER_FAILED", job.ErrorCode);
        Assert.Equal(0, job.RetryCount);
        Assert.NotEqual(ImportJobStatus.Processing, job.Status);
        Assert.NotEqual(0, childId);
    }

    [Fact]
    public async Task AWorkerThatNeverFinishes_EndsTheJobAsFailedWithPdfTimeout()
    {
        var extractor = new ChildProcessPdfTextExtractor(IdleWorker(onStarted: null, timeout: TimeSpan.FromSeconds(1)));
        var (backgroundJob, jobs) = Build(extractor);
        var job = ImportJob.Create(CoupleId, UserId, "couples/x/statement.pdf", "application/pdf", FixedNow);
        jobs.Jobs.Add(job);

        await backgroundJob.ProcessPendingJobsAsync(CancellationToken.None);

        Assert.Equal(ImportJobStatus.Failed, job.Status);
        Assert.Equal("PDF_TIMEOUT", job.ErrorCode);
    }

    private static PdfWorkerOptions IdleWorker(Action<int>? onStarted, TimeSpan? timeout = null) => OperatingSystem.IsWindows()
        ? new PdfWorkerOptions { FileName = "cmd.exe", Arguments = ["/c", "ping -n 60 127.0.0.1 > nul"], Timeout = timeout ?? TimeSpan.FromSeconds(30), OnProcessStarted = onStarted }
        : new PdfWorkerOptions { FileName = "sh", Arguments = ["-c", "sleep 60"], Timeout = timeout ?? TimeSpan.FromSeconds(30), OnProcessStarted = onStarted };

    private static (OcrBackgroundJob Job, FakeImportJobRepository Jobs) Build(IPdfTextExtractor extractor)
    {
        var jobs = new FakeImportJobRepository();
        var provider = new LocalPdfParserProvider(
            new PdfStorage(), extractor, new BankDetector([]), NullLogger<LocalPdfParserProvider>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IImportJobRepository>(jobs);
        services.AddSingleton<IOcrProvider>(provider);
        services.AddSingleton<IStorageAdapter>(new PdfStorage());
        services.AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider(FixedNow));
        services.AddSingleton(new OcrProcessingService(
            new UniqueIndexTransactionRepository(), new NullCategoryClassifier(), new FakeBudgetRepository()));

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new OcrBackgroundJob(scopeFactory, NullLogger<OcrBackgroundJob>.Instance), jobs);
    }

    private sealed class PdfStorage : IStorageAdapter
    {
        public Task<string> UploadAsync(Guid coupleId, Guid uploadId, Stream content, string mimeType, CancellationToken ct)
            => Task.FromResult($"couples/{coupleId}/{uploadId}");

        public Task<Stream> DownloadAsync(string storagePath, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream(SyntheticPdf.Pages(2)));

        public Task DeleteAsync(string storagePath, CancellationToken ct) => Task.CompletedTask;
    }
}

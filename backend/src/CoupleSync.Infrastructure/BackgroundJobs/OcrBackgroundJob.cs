using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.OcrImport;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Infrastructure.BackgroundJobs;

public sealed class OcrBackgroundJob : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    /// <summary>Total number of processing attempts for transient failures (first try included).</summary>
    private const int MaxAttempts = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OcrBackgroundJob> _logger;

    public OcrBackgroundJob(IServiceScopeFactory scopeFactory, ILogger<OcrBackgroundJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OcrBackgroundJob started.");

        // The API sleeps and restarts on the free tier; a job that was Processing at that moment would
        // never be picked up again, so it is failed (with a clear error) as soon as the worker starts.
        try
        {
            await RecoverStuckJobsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not recover import jobs stuck in Processing at startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in OcrBackgroundJob poll loop.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OcrBackgroundJob stopped.");
    }

    /// <summary>Fails import jobs left in Processing beyond the timeout. Public so it can be unit-tested.</summary>
    public async Task<int> RecoverStuckJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var recovery = new ImportJobRecovery(
            scope.ServiceProvider.GetRequiredService<IImportJobRepository>(),
            scope.ServiceProvider.GetRequiredService<IStorageAdapter>(),
            scope.ServiceProvider.GetRequiredService<IDateTimeProvider>(),
            _logger);

        var recovered = await recovery.RecoverAllAsync(ct);
        if (recovered > 0)
            _logger.LogWarning("{Count} import job(s) stuck in Processing were marked as failed.", recovered);
        return recovered;
    }

    /// <summary>Runs one polling pass over the pending jobs. Public so the pass can be unit-tested.</summary>
    public async Task ProcessPendingJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IImportJobRepository>();
        var ocrProvider = scope.ServiceProvider.GetRequiredService<IOcrProvider>();
        var ocrProcessingService = scope.ServiceProvider.GetRequiredService<OcrProcessingService>();
        var storageAdapter = scope.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var dateTimeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();

        var pendingJobs = await repo.GetPendingAsync(5, ct);

        foreach (var job in pendingJobs)
        {
            if (ct.IsCancellationRequested) break;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                _logger.LogInformation("OCR job {IngestId} starting (mimeType={MimeType})", job.Id, job.FileMimeType);

                job.MarkProcessing(dateTimeProvider.UtcNow);
                await repo.SaveChangesAsync(ct);

                var rawOcrJson = await ocrProvider.AnalyzeAsync(job.StoragePath, job.FileMimeType, ct);

                var candidates = await ocrProcessingService.ParseAndDeduplicateAsync(job.CoupleId, rawOcrJson, ct);
                var candidatesJson = OcrProcessingService.SerializeCandidates(candidates);

                job.MarkReady(candidatesJson, dateTimeProvider.UtcNow);
                await repo.SaveChangesAsync(ct);

                _logger.LogInformation("OCR job {IngestId} completed in {ElapsedMs}ms with {TransactionCount} candidates", job.Id, sw.ElapsedMilliseconds, candidates.Count);

                // Delete the uploaded file only after successful processing
                await TryDeleteFileAsync(storageAdapter, job.StoragePath, job.Id, ct);
            }
            catch (OcrQuotaExhaustedException ex)
            {
                _logger.LogWarning(
                    "OCR quota exhausted for job {JobId} after {ElapsedMs}ms. ResetDate={ResetDate}",
                    job.Id, sw.ElapsedMilliseconds, ex.QuotaResetDate);

                job.MarkFailed("quota_exhausted", ex.Message, dateTimeProvider.UtcNow, ex.QuotaResetDate);
                await repo.SaveChangesAsync(ct);
                await TryDeleteFileAsync(storageAdapter, job.StoragePath, job.Id, ct);

                // Stop processing remaining jobs — quota is exhausted for all
                break;
            }
            catch (AppException ex)
            {
                // Business failures (OcrException, BankFormatUnknownException, ...) are deterministic:
                // the same file fails the same way every time, so retrying only delays the answer.
                _logger.LogWarning(ex, "OCR job {JobId} failed with code {Code} after {ElapsedMs}ms", job.Id, ex.Code, sw.ElapsedMilliseconds);
                job.MarkFailed(ex.Code, ex.Message, dateTimeProvider.UtcNow);
                await repo.SaveChangesAsync(ct);
                await TryDeleteFileAsync(storageAdapter, job.StoragePath, job.Id, ct);
            }
            catch (Exception ex)
            {
                var attempt = job.RetryCount + 1;

                _logger.LogError(ex, "OCR processing failed for job {JobId} after {ElapsedMs}ms (attempt {Attempt}/{MaxAttempts})",
                    job.Id, sw.ElapsedMilliseconds, attempt, MaxAttempts);

                if (attempt < MaxAttempts)
                {
                    job.ResetForRetry(dateTimeProvider.UtcNow);
                    await repo.SaveChangesAsync(ct);
                }
                else
                {
                    job.MarkFailed("processing_error", "Internal processing error after max retries.", dateTimeProvider.UtcNow);
                    await repo.SaveChangesAsync(ct);
                    await TryDeleteFileAsync(storageAdapter, job.StoragePath, job.Id, ct);
                }
            }
        }
    }

    private async Task TryDeleteFileAsync(IStorageAdapter storage, string storagePath, Guid jobId, CancellationToken ct)
    {
        try
        {
            await storage.DeleteAsync(storagePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete uploaded file for job {JobId} at path '{Path}'. Manual cleanup may be required.", jobId, storagePath);
        }
    }
}

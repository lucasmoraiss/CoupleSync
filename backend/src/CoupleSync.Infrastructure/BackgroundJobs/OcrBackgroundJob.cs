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
    private const int MaxRetries = 3;

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

    private async Task ProcessPendingJobsAsync(CancellationToken ct)
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
            catch (OcrException ex)
            {
                _logger.LogWarning(ex, "OCR job {JobId} failed with code {Code} after {ElapsedMs}ms", job.Id, ex.Code, sw.ElapsedMilliseconds);
                job.MarkFailed(ex.Code, ex.Message, dateTimeProvider.UtcNow);
                await repo.SaveChangesAsync(ct);
                await TryDeleteFileAsync(storageAdapter, job.StoragePath, job.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR processing failed for job {JobId} after {ElapsedMs}ms (attempt {Attempt}/{MaxRetries})",
                    job.Id, sw.ElapsedMilliseconds, job.RetryCount + 1, MaxRetries);

                if (job.CanRetry(MaxRetries))
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

using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OcrImport;

/// <summary>
/// Fails import jobs that were left in Processing (the API restarted or the worker died mid-job), so the
/// user gets a clear error and can send the file again instead of waiting forever.
/// Runs when a job is read and once at API startup.
/// </summary>
public sealed class ImportJobRecovery
{
    /// <summary>How long a job may stay in Processing before it is considered abandoned.</summary>
    public static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(10);

    public const string ProcessingTimeoutCode = "PROCESSING_TIMEOUT";

    public const string ProcessingTimeoutMessage =
        "O processamento do extrato demorou mais que o esperado e foi interrompido. Envie o arquivo novamente.";

    private readonly IImportJobRepository _repository;
    private readonly IStorageAdapter _storage;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger _logger;

    public ImportJobRecovery(
        IImportJobRepository repository,
        IStorageAdapter storage,
        IDateTimeProvider dateTimeProvider,
        ILogger logger)
    {
        _repository = repository;
        _storage = storage;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
    }

    /// <summary>Marks <paramref name="job"/> as failed when it is stuck in Processing. Returns true when it did.</summary>
    public async Task<bool> FailIfStuckAsync(ImportJob job, CancellationToken ct)
    {
        var now = _dateTimeProvider.UtcNow;
        if (!job.IsStuckProcessing(now, ProcessingTimeout))
            return false;

        job.MarkFailed(ProcessingTimeoutCode, ProcessingTimeoutMessage, now);
        await _repository.SaveChangesAsync(ct);
        await TryDeleteFileAsync(job, ct);

        _logger.LogWarning("Import job {JobId} was stuck in Processing and was marked as failed.", job.Id);
        return true;
    }

    /// <summary>Fails every stuck job. Returns how many were recovered.</summary>
    public async Task<int> RecoverAllAsync(CancellationToken ct)
    {
        var cutoff = _dateTimeProvider.UtcNow - ProcessingTimeout;
        var stuck = await _repository.GetStuckProcessingAsync(cutoff, 100, ct);

        var recovered = 0;
        foreach (var job in stuck)
        {
            if (await FailIfStuckAsync(job, ct))
                recovered++;
        }

        return recovered;
    }

    private async Task TryDeleteFileAsync(ImportJob job, CancellationToken ct)
    {
        try
        {
            await _storage.DeleteAsync(job.StoragePath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete uploaded file of stuck import job {JobId}.", job.Id);
        }
    }
}

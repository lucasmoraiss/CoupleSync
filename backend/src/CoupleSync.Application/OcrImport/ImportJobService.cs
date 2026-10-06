using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OcrImport;

public sealed class ImportJobService
{
    private const string OcrBank = "OCR";

    private readonly IImportJobRepository _repository;
    private readonly IStorageAdapter _storageAdapter;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITransactionRepository _transactionRepository;
    private readonly INotificationCaptureRepository _ingestRepository;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly INotificationSettingsRepository _notificationSettingsRepository;
    private readonly ILogger<ImportJobService> _logger;

    public ImportJobService(
        IImportJobRepository repository,
        IStorageAdapter storageAdapter,
        IDateTimeProvider dateTimeProvider,
        ITransactionRepository transactionRepository,
        INotificationCaptureRepository ingestRepository,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEventRepository,
        INotificationSettingsRepository notificationSettingsRepository,
        ILogger<ImportJobService> logger)
    {
        _repository = repository;
        _storageAdapter = storageAdapter;
        _dateTimeProvider = dateTimeProvider;
        _transactionRepository = transactionRepository;
        _ingestRepository = ingestRepository;
        _alertPolicyService = alertPolicyService;
        _notificationEventRepository = notificationEventRepository;
        _notificationSettingsRepository = notificationSettingsRepository;
        _logger = logger;
    }

    /// <summary>
    /// Uploads the file to storage, creates an ImportJob, and returns the job ID (upload_id).
    /// </summary>
    public async Task<Guid> UploadAsync(
        Guid coupleId,
        Guid userId,
        Stream fileStream,
        string detectedMimeType,
        CancellationToken ct)
    {
        var uploadId = Guid.NewGuid();
        var storagePath = await _storageAdapter.UploadAsync(
            coupleId, uploadId, fileStream, detectedMimeType, ct);

        var job = ImportJob.Create(
            coupleId,
            userId,
            storagePath,
            detectedMimeType,
            _dateTimeProvider.UtcNow);

        await _repository.AddAsync(job, ct);
        await _repository.SaveChangesAsync(ct);

        return job.Id;
    }

    /// <summary>
    /// Returns the ImportJob for the given uploadId scoped to coupleId, or null if not found.
    /// </summary>
    public Task<ImportJob?> GetJobAsync(Guid uploadId, Guid coupleId, CancellationToken ct)
        => _repository.GetByIdAsync(uploadId, coupleId, ct);

    /// <summary>
    /// Returns the parsed OCR candidates when status is Ready.
    /// Returns null if the job does not belong to coupleId.
    /// Throws <see cref="ConflictException"/> with OCR_JOB_NOT_READY if status is not Ready.
    /// </summary>
    public async Task<IReadOnlyList<OcrCandidate>?> GetCandidatesAsync(
        Guid uploadId, Guid coupleId, CancellationToken ct)
    {
        var job = await _repository.GetByIdAsync(uploadId, coupleId, ct);
        if (job is null) return null;

        if (job.Status != Domain.Entities.ImportJobStatus.Ready)
            throw new ConflictException(
                job.Status == Domain.Entities.ImportJobStatus.Confirmed
                    ? "OCR_JOB_ALREADY_CONFIRMED"
                    : "OCR_JOB_NOT_READY",
                job.Status == Domain.Entities.ImportJobStatus.Confirmed
                    ? "Esta importação já foi confirmada."
                    : "A leitura do extrato ainda não terminou.");

        return JsonSerializer.Deserialize<List<OcrCandidate>>(job.OcrResultJson!) ?? new List<OcrCandidate>();
    }

    /// <summary>
    /// Creates <see cref="Transaction"/> records for the selected OCR candidate indices.
    /// Returns null if the job does not belong to coupleId (caller should return 404).
    /// </summary>
    public async Task<ConfirmCandidatesResult?> ConfirmCandidatesAsync(
        Guid uploadId,
        Guid coupleId,
        Guid userId,
        IReadOnlyList<int> selectedIndices,
        IReadOnlyDictionary<int, string>? categoryOverrides,
        CancellationToken ct,
        IReadOnlyDictionary<int, CandidateEdit>? candidateEdits = null)
    {
        if (selectedIndices is null || selectedIndices.Count == 0)
            throw new UnprocessableEntityException("INVALID_SELECTION", "Selecione pelo menos uma transação.");

        var candidates = await GetCandidatesAsync(uploadId, coupleId, ct);
        if (candidates is null) return null;

        // Every selected index must exist. Rejecting here (before touching the job) keeps the
        // candidates reachable instead of silently confirming an empty import.
        var byIndex = candidates.ToDictionary(c => c.Index);
        var requested = selectedIndices.Distinct().ToList();
        var unknown = requested.Where(i => !byIndex.ContainsKey(i)).ToList();
        if (unknown.Count > 0)
            throw new UnprocessableEntityException(
                "INVALID_SELECTION",
                $"Transação selecionada não existe nesta importação: {string.Join(", ", unknown)}.");

        // Edits may only target lines that are part of this confirmation.
        if (candidateEdits is not null)
        {
            var strayEdits = candidateEdits.Keys.Where(i => !requested.Contains(i)).ToList();
            if (strayEdits.Count > 0)
                throw new UnprocessableEntityException(
                    "INVALID_SELECTION",
                    $"Transação editada não está entre as selecionadas: {string.Join(", ", strayEdits)}.");
        }

        var fingerprints = ResolveFingerprints(coupleId, candidates);
        var selected = requested.OrderBy(i => i).Select(i => byIndex[i]).ToList();

        var ingests = new List<TransactionEventIngest>();
        var created = new List<Transaction>();
        var duplicatesSkipped = 0;
        var now = _dateTimeProvider.UtcNow;

        foreach (var candidate in selected)
        {
            var fingerprint = fingerprints[candidate.Index];

            // The fingerprint always comes from the line as read from the statement, never from the
            // user's edits, so re-importing the same file still recognises it as a duplicate.
            // Already imported (same statement confirmed before): skip the line, keep the batch.
            if (await _transactionRepository.FingerprintExistsAsync(fingerprint, coupleId, ct))
            {
                duplicatesSkipped++;
                continue;
            }

            var category = "Outros";
            if (categoryOverrides is not null && categoryOverrides.TryGetValue(candidate.Index, out var userCategory)
                && !string.IsNullOrWhiteSpace(userCategory))
            {
                category = userCategory;
            }
            else if (!string.IsNullOrWhiteSpace(candidate.SuggestedCategory))
            {
                category = candidate.SuggestedCategory;
            }

            // Corrections typed on the review screen win over what was read from the statement.
            var amount = candidate.Amount;
            var description = candidate.Description;
            if (candidateEdits is not null && candidateEdits.TryGetValue(candidate.Index, out var edit))
            {
                if (edit.Amount.HasValue) amount = edit.Amount.Value;
                if (!string.IsNullOrWhiteSpace(edit.Description)) description = edit.Description.Trim();
            }

            var ingest = TransactionEventIngest.Create(
                coupleId: coupleId,
                userId: userId,
                bank: OcrBank,
                amount: amount,
                currency: candidate.Currency,
                eventTimestamp: candidate.Date,
                description: description,
                merchant: null,
                rawNotificationTextRedacted: null,
                createdAtUtc: now);

            ingests.Add(ingest);

            var txn = Transaction.Create(
                coupleId: coupleId,
                userId: userId,
                fingerprint: fingerprint,
                bank: "OCR Import",
                amount: amount,
                currency: candidate.Currency,
                eventTimestampUtc: candidate.Date,
                description: description,
                merchant: null,
                category: category,
                ingestEventId: ingest.Id,
                createdAtUtc: now,
                source: TransactionSource.OcrImport);

            created.Add(txn);
        }

        // The job goes to Confirmed in the same unit of work as the inserts (repositories share the
        // scoped DbContext), so a failed insert never leaves a half-confirmed import behind.
        var job = await _repository.GetByIdAsync(uploadId, coupleId, ct);

        try
        {
            if (created.Count > 0)
            {
                await _ingestRepository.AddIngestEventsRangeAsync(ingests, ct);
                await _transactionRepository.AddTransactionsRangeAsync(created, ct);
            }

            job!.MarkConfirmed(now);
            await _transactionRepository.SaveChangesAsync(ct);
            await _repository.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two confirmations raced past the duplicate check; the unique index on
            // (couple_id, fingerprint) let only one of them through.
            throw new ConflictException(
                "OCR_CONFIRM_CONFLICT",
                "Essas transações já foram importadas por outra requisição. Atualize e tente novamente.");
        }

        // Fire-and-not-propagate: alert policy evaluation after successful transaction persist.
        try
        {
            var nowUtc = _dateTimeProvider.UtcNow;
            var since = nowUtc.AddDays(-30);
            // No row in notification_settings means the user never changed anything: the defaults
            // (every alert enabled) apply, exactly as GET /notifications/settings reports them.
            var settings = await _notificationSettingsRepository.GetByUserIdAsync(userId, coupleId, ct)
                ?? NotificationSettings.Create(userId, coupleId, nowUtc);
            var recentTransactions = await _transactionRepository.GetRecentByCoupleAsync(coupleId, since, ct);
            foreach (var txn in created)
            {
                var alertEvents = await _alertPolicyService.EvaluatePostIngestAsync(
                    coupleId, userId, txn, recentTransactions, settings, nowUtc, ct);
                if (alertEvents.Count > 0)
                {
                    await _notificationEventRepository.AddRangeAsync(alertEvents, ct);
                    await _notificationEventRepository.SaveChangesAsync(ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert policy evaluation failed for couple {CoupleId}", coupleId);
        }

        return new ConfirmCandidatesResult(created, duplicatesSkipped);
    }

    /// <summary>
    /// Fingerprint to store for each candidate. Jobs processed before repeated lines received an
    /// occurrence ordinal may carry the same fingerprint more than once; the repetitions are
    /// re-derived here (in statement order) exactly as <see cref="OcrProcessingService"/> does now.
    /// </summary>
    private static Dictionary<int, string> ResolveFingerprints(Guid coupleId, IReadOnlyList<OcrCandidate> candidates)
    {
        var result = new Dictionary<int, string>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates.OrderBy(c => c.Index))
        {
            var occurrence = seen.GetValueOrDefault(candidate.Fingerprint) + 1;
            seen[candidate.Fingerprint] = occurrence;

            result[candidate.Index] = occurrence == 1
                ? candidate.Fingerprint
                : OcrProcessingService.ComputeFingerprint(
                    coupleId, candidate.Date, candidate.Amount, candidate.Description, occurrence);
        }

        return result;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("23505", StringComparison.Ordinal)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>User correction for one selected candidate; null fields keep the value read from the statement.</summary>
public sealed record CandidateEdit(string? Description, decimal? Amount);

/// <summary>Outcome of confirming an import: what was stored and how many lines were skipped as already imported.</summary>
public sealed record ConfirmCandidatesResult(IReadOnlyList<Transaction> Created, int DuplicatesSkipped);

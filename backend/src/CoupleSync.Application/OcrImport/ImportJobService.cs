using CoupleSync.Application.Common;
using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;
using CoupleSync.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace CoupleSync.Application.OcrImport;

public sealed class ImportJobService
{
    private const string OcrBank = TransactionEventIngest.OcrBank;

    private readonly IImportJobRepository _repository;
    private readonly IStorageAdapter _storageAdapter;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITransactionRepository _transactionRepository;
    private readonly INotificationCaptureRepository _ingestRepository;
    private readonly IAlertPolicyService _alertPolicyService;
    private readonly INotificationEventRepository _notificationEventRepository;
    private readonly ILogger<ImportJobService> _logger;
    private readonly ImportJobRecovery _recovery;

    public ImportJobService(
        IImportJobRepository repository,
        IStorageAdapter storageAdapter,
        IDateTimeProvider dateTimeProvider,
        ITransactionRepository transactionRepository,
        INotificationCaptureRepository ingestRepository,
        IAlertPolicyService alertPolicyService,
        INotificationEventRepository notificationEventRepository,
        ILogger<ImportJobService> logger)
    {
        _repository = repository;
        _storageAdapter = storageAdapter;
        _dateTimeProvider = dateTimeProvider;
        _transactionRepository = transactionRepository;
        _ingestRepository = ingestRepository;
        _alertPolicyService = alertPolicyService;
        _notificationEventRepository = notificationEventRepository;
        _logger = logger;
        _recovery = new ImportJobRecovery(repository, storageAdapter, dateTimeProvider, logger);
    }

    /// <summary>
    /// Uploads the file to storage, creates an ImportJob, and returns the job ID (upload_id).
    /// </summary>
    public async Task<Guid> UploadAsync(
        Guid coupleId,
        Guid userId,
        Stream fileStream,
        string detectedMimeType,
        CancellationToken ct,
        string? fileName = null,
        bool aiCategorizationConsent = false)
    {
        var uploadId = Guid.NewGuid();
        var storagePath = await _storageAdapter.UploadAsync(
            coupleId, uploadId, fileStream, detectedMimeType, ct);

        var job = ImportJob.Create(
            coupleId,
            userId,
            storagePath,
            detectedMimeType,
            _dateTimeProvider.UtcNow,
            fileName,
            aiCategorizationConsent);

        await _repository.AddAsync(job, ct);
        await _repository.SaveChangesAsync(ct);

        return job.Id;
    }

    /// <summary>
    /// Returns the ImportJob for the given uploadId scoped to coupleId, or null if not found.
    /// A job abandoned in Processing (restart, crash) is failed here, so the app stops waiting for it.
    /// </summary>
    public async Task<ImportJob?> GetJobAsync(Guid uploadId, Guid coupleId, CancellationToken ct)
    {
        var job = await _repository.GetByIdAsync(uploadId, coupleId, ct);
        if (job is not null)
            await _recovery.FailIfStuckAsync(job, ct);
        return job;
    }

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
    /// The couple's imports that still have debit lines waiting for review (newest first, at most 20).
    /// </summary>
    public async Task<IReadOnlyList<OpenImport>> GetOpenImportsAsync(Guid coupleId, CancellationToken ct)
    {
        var jobs = await _repository.GetReadyByCoupleAsync(coupleId, 20, ct);
        var result = new List<OpenImport>();

        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.OcrResultJson)) continue;

            var candidates = JsonSerializer.Deserialize<List<OcrCandidate>>(job.OcrResultJson) ?? new();
            var debits = candidates.Where(c => c.Type != TransactionType.Credit).ToList();
            var pending = debits.Count(c => job.GetLineState(c.Index) == ImportLineState.Pending);
            if (pending == 0) continue;

            result.Add(new OpenImport(
                job.Id, job.SourceFileName, job.CreatedAtUtc, pending, debits.Count,
                candidates.Count - debits.Count));
        }

        return result;
    }

    /// <summary>
    /// Candidates of a Ready job together with the review state of each line.
    /// Returns null if the job does not belong to coupleId; same conflicts as <see cref="GetCandidatesAsync"/>.
    /// </summary>
    public async Task<ImportReview?> GetReviewAsync(Guid uploadId, Guid coupleId, CancellationToken ct)
    {
        var candidates = await GetCandidatesAsync(uploadId, coupleId, ct);
        if (candidates is null) return null;

        var job = await _repository.GetByIdAsync(uploadId, coupleId, ct);
        var lines = candidates
            .Where(c => c.Type != TransactionType.Credit)
            .Select(c => new ImportReviewLine(c, job!.GetLineState(c.Index)))
            .ToList();
        var credits = candidates.Where(c => c.Type == TransactionType.Credit).ToList();

        return new ImportReview(lines, credits);
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
        IReadOnlyDictionary<int, CandidateEdit>? candidateEdits = null,
        bool keepJobOpen = false,
        IReadOnlyList<int>? discardedIndices = null)
    {
        selectedIndices ??= [];
        discardedIndices ??= [];
        if (selectedIndices.Count == 0 && discardedIndices.Count == 0)
            throw new UnprocessableEntityException("INVALID_SELECTION", "Selecione pelo menos uma transação.");

        var candidates = await GetCandidatesAsync(uploadId, coupleId, ct);
        if (candidates is null) return null;

        // Every selected index must exist. Rejecting here (before touching the job) keeps the
        // candidates reachable instead of silently confirming an empty import.
        var byIndex = candidates.ToDictionary(c => c.Index);
        var requested = selectedIndices.Distinct().ToList();
        var discarded = discardedIndices.Distinct().ToList();
        var unknown = requested.Concat(discarded).Where(i => !byIndex.ContainsKey(i)).ToList();
        if (unknown.Count > 0)
            throw new UnprocessableEntityException(
                "INVALID_SELECTION",
                $"Transação selecionada não existe nesta importação: {string.Join(", ", unknown)}.");

        // Credits (entradas) are listed for information only: they never become transactions.
        var credits = requested.Concat(discarded).Where(i => byIndex[i].Type == TransactionType.Credit).ToList();
        if (credits.Count > 0)
            throw new UnprocessableEntityException(
                "INVALID_SELECTION",
                $"Entradas não são importadas como despesa: {string.Join(", ", credits)}.");

        var both = requested.Intersect(discarded).ToList();
        if (both.Count > 0)
            throw new UnprocessableEntityException(
                "INVALID_SELECTION",
                $"Transação não pode ser importada e descartada ao mesmo tempo: {string.Join(", ", both)}.");

        // Edits may only target lines that are part of this confirmation.
        if (candidateEdits is not null)
        {
            var strayEdits = candidateEdits.Keys.Where(i => !requested.Contains(i)).ToList();
            if (strayEdits.Count > 0)
                throw new UnprocessableEntityException(
                    "INVALID_SELECTION",
                    $"Transação editada não está entre as selecionadas: {string.Join(", ", strayEdits)}.");
        }

        var debits = candidates.Where(c => c.Type != TransactionType.Credit).ToList();
        var fingerprints = ResolveFingerprints(coupleId, debits);
        var selected = requested.OrderBy(i => i).Select(i => byIndex[i]).ToList();
        var settled = new List<int>();
        var job = (await _repository.GetByIdAsync(uploadId, coupleId, ct))!;

        var ingests = new List<TransactionEventIngest>();
        var created = new List<Transaction>();
        var duplicatesSkipped = 0;
        var now = _dateTimeProvider.UtcNow;

        foreach (var candidate in selected)
        {
            // Line confirmed by an earlier (partial) confirmation: never stored twice.
            if (job.GetLineState(candidate.Index) == ImportLineState.Confirmed)
            {
                duplicatesSkipped++;
                continue;
            }

            var fingerprint = fingerprints[candidate.Index];

            // The fingerprint always comes from the line as read from the statement, never from the
            // user's edits, so re-importing the same file still recognises it as a duplicate.
            // Already imported (same statement confirmed before): skip the line, keep the batch.
            if (await _transactionRepository.FingerprintExistsAsync(fingerprint, coupleId, ct))
            {
                duplicatesSkipped++;
                settled.Add(candidate.Index);
                continue;
            }

            var category = TransactionCategories.Other;
            if (categoryOverrides is not null && categoryOverrides.TryGetValue(candidate.Index, out var userCategory)
                && !string.IsNullOrWhiteSpace(userCategory))
            {
                // The installed app has a free-text category field here: unknown text becomes OUTROS, not a 400.
                category = TransactionCategories.NormalizeOrOther(userCategory);
            }
            else if (!string.IsNullOrWhiteSpace(candidate.SuggestedCategory))
            {
                category = TransactionCategories.NormalizeOrOther(candidate.SuggestedCategory);
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
            settled.Add(candidate.Index);
        }

        // Line states and the job status change in the same unit of work as the inserts (repositories
        // share the scoped DbContext), so a failed insert never leaves a half-confirmed import behind.
        var remaining = 0;

        try
        {
            if (created.Count > 0)
            {
                await _ingestRepository.AddIngestEventsRangeAsync(ingests, ct);
                await _transactionRepository.AddTransactionsRangeAsync(created, ct);
            }

            foreach (var index in settled)
                job.SetLineState(index, ImportLineState.Confirmed, now);
            foreach (var index in discarded)
                job.SetLineState(index, ImportLineState.Discarded, now);

            // Without keepJobOpen (the app's single confirm call) whatever was not selected is dropped
            // and the job closes. With it, the lines left over can be confirmed or discarded later.
            if (!keepJobOpen)
            {
                foreach (var line in debits.Where(c => job.GetLineState(c.Index) == ImportLineState.Pending))
                    job.SetLineState(line.Index, ImportLineState.Discarded, now);
            }

            remaining = debits.Count(c => job.GetLineState(c.Index) == ImportLineState.Pending);
            if (remaining == 0)
                job.MarkConfirmed(now);

            await _transactionRepository.SaveChangesAsync(ct);
            await _repository.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Another confirmation changed this import first (the job row is a concurrency token). Nothing of
            // this request was stored; refreshing shows what the other request did.
            throw new ConflictException(
                "OCR_CONFIRM_CONFLICT",
                "Esta importação foi alterada por outra requisição. Atualize e tente novamente.");
        }
        catch (UniqueViolationException)
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
            var recentTransactions = await _transactionRepository.GetRecentByCoupleAsync(coupleId, since, ct);
            foreach (var txn in created)
            {
                var alertEvents = await _alertPolicyService.EvaluatePostIngestAsync(
                    coupleId, txn, recentTransactions, nowUtc, ct);
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

        return new ConfirmCandidatesResult(created, duplicatesSkipped, remaining);
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

}

/// <summary>User correction for one selected candidate; null fields keep the value read from the statement.</summary>
public sealed record CandidateEdit(string? Description, decimal? Amount);

/// <summary>Outcome of confirming an import: what was stored and how many lines were skipped as already imported.</summary>
public sealed record ConfirmCandidatesResult(IReadOnlyList<Transaction> Created, int DuplicatesSkipped, int RemainingLines = 0);

/// <summary>An import with lines still waiting for review, as listed on the import entry screen.</summary>
public sealed record OpenImport(
    Guid UploadId, string? FileName, DateTime CreatedAtUtc, int PendingLines, int TotalLines, int CreditsCount);

/// <summary>A debit line of the review with what already happened to it.</summary>
public sealed record ImportReviewLine(OcrCandidate Candidate, ImportLineState State);

/// <summary>What the review screen shows: confirmable debit lines plus the credits that are only listed.</summary>
public sealed record ImportReview(IReadOnlyList<ImportReviewLine> Lines, IReadOnlyList<OcrCandidate> Credits);

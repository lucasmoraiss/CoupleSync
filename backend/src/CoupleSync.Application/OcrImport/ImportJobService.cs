using System.Text.Json;
using CoupleSync.Application.Common.Exceptions;
using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Application.OcrImport;

public sealed class ImportJobService
{
    private readonly IImportJobRepository _repository;
    private readonly IStorageAdapter _storageAdapter;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ITransactionRepository _transactionRepository;

    public ImportJobService(
        IImportJobRepository repository,
        IStorageAdapter storageAdapter,
        IDateTimeProvider dateTimeProvider,
        ITransactionRepository transactionRepository)
    {
        _repository = repository;
        _storageAdapter = storageAdapter;
        _dateTimeProvider = dateTimeProvider;
        _transactionRepository = transactionRepository;
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
                    ? "This import has already been confirmed."
                    : "OCR processing is not complete yet.");

        return JsonSerializer.Deserialize<List<OcrCandidate>>(job.OcrResultJson!) ?? new List<OcrCandidate>();
    }

    /// <summary>
    /// Creates <see cref="Transaction"/> records for the selected OCR candidate indices.
    /// Returns null if the job does not belong to coupleId (caller should return 404).
    /// </summary>
    public async Task<IReadOnlyList<Transaction>?> ConfirmCandidatesAsync(
        Guid uploadId,
        Guid coupleId,
        Guid userId,
        IReadOnlyList<int> selectedIndices,
        IReadOnlyDictionary<int, string>? categoryOverrides,
        CancellationToken ct)
    {
        if (selectedIndices is null || selectedIndices.Count == 0)
            throw new UnprocessableEntityException("INVALID_SELECTION", "At least one candidate index must be selected.");

        var candidates = await GetCandidatesAsync(uploadId, coupleId, ct);
        if (candidates is null) return null;

        var selected = candidates.Where(c => selectedIndices.Contains(c.Index)).ToList();
        var created = new List<Transaction>();
        var now = _dateTimeProvider.UtcNow;

        foreach (var candidate in selected)
        {
            // Use user-provided category override if present, otherwise fall back to AI suggestion or default
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

            var txn = Transaction.Create(
                coupleId: coupleId,
                userId: userId,
                fingerprint: candidate.Fingerprint,
                bank: "OCR Import",
                amount: candidate.Amount,
                currency: candidate.Currency,
                eventTimestampUtc: candidate.Date,
                description: candidate.Description,
                merchant: null,
                category: category,
                ingestEventId: Guid.Empty,
                createdAtUtc: now);

            await _transactionRepository.AddTransactionAsync(txn, ct);
            created.Add(txn);
        }

        await _transactionRepository.SaveChangesAsync(ct);

        // Transition job to Confirmed to prevent duplicate confirm calls
        var job = await _repository.GetByIdAsync(uploadId, coupleId, ct);
        job!.MarkConfirmed(_dateTimeProvider.UtcNow);
        await _repository.SaveChangesAsync(ct);

        return created;
    }
}

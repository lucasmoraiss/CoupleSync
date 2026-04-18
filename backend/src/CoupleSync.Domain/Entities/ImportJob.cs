using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed class ImportJob : ICoupleScoped
{
    private ImportJob() { }

    private ImportJob(
        Guid id,
        Guid coupleId,
        Guid userId,
        string storagePath,
        string fileMimeType,
        DateTime createdAtUtc)
    {
        Id = id;
        CoupleId = coupleId;
        UserId = userId;
        StoragePath = storagePath;
        FileMimeType = fileMimeType;
        Status = ImportJobStatus.Pending;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>
    /// Internal-only storage path. Must never be exposed to API clients.
    /// </summary>
    public string StoragePath { get; private set; } = string.Empty;

    public string FileMimeType { get; private set; } = string.Empty;
    public ImportJobStatus Status { get; private set; }

    /// <summary>
    /// Raw OCR result stored as JSON. Null until job reaches Ready state.
    /// </summary>
    public string? OcrResultJson { get; private set; }

    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime? QuotaResetDate { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static ImportJob Create(
        Guid coupleId,
        Guid userId,
        string storagePath,
        string fileMimeType,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentException("StoragePath is required.", nameof(storagePath));

        if (string.IsNullOrWhiteSpace(fileMimeType))
            throw new ArgumentException("FileMimeType is required.", nameof(fileMimeType));

        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        return new ImportJob(Guid.NewGuid(), coupleId, userId, storagePath, fileMimeType, createdAtUtc);
    }

    public void MarkProcessing(DateTime nowUtc)
    {
        Status = ImportJobStatus.Processing;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkReady(string ocrResultJson, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ocrResultJson))
            throw new ArgumentException("OcrResultJson is required when marking Ready.", nameof(ocrResultJson));

        OcrResultJson = ocrResultJson;
        Status = ImportJobStatus.Ready;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkFailed(string errorCode, string errorMessage, DateTime nowUtc, DateTime? quotaResetDate = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("ErrorCode is required when marking Failed.", nameof(errorCode));

        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        QuotaResetDate = quotaResetDate;
        Status = ImportJobStatus.Failed;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkConfirmed(DateTime nowUtc)
    {
        Status = ImportJobStatus.Confirmed;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    private static DateTime NormalizeUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
}

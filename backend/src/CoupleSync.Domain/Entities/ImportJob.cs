using System.Text.Json;
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

    /// <summary>Name of the file the user sent (shown in the list of open imports). Null for older jobs.</summary>
    public string? SourceFileName { get; private set; }

    public ImportJobStatus Status { get; private set; }

    /// <summary>
    /// Raw OCR result stored as JSON. Null until job reaches Ready state.
    /// </summary>
    public string? OcrResultJson { get; private set; }

    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime? QuotaResetDate { get; private set; }
    public int RetryCount { get; private set; }

    /// <summary>
    /// Per-line outcome of the review, as JSON {"index":"Confirmed"|"Discarded"}. A line that is absent is
    /// still pending. Null for jobs that never had a partial confirmation (all existing jobs).
    /// </summary>
    public string? LineStatesJson { get; private set; }

    /// <summary>
    /// The uploader had accepted the AI disclosure when sending the file. Only then may the statement lines'
    /// descriptions be sent to Gemini for categorization. False for every job created before this existed.
    /// </summary>
    public bool AiCategorizationConsent { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static ImportJob Create(
        Guid coupleId,
        Guid userId,
        string storagePath,
        string fileMimeType,
        DateTime createdAtUtc,
        string? sourceFileName = null,
        bool aiCategorizationConsent = false)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
            throw new ArgumentException("O caminho do arquivo é obrigatório.", nameof(storagePath));

        if (string.IsNullOrWhiteSpace(fileMimeType))
            throw new ArgumentException("O tipo do arquivo é obrigatório.", nameof(fileMimeType));

        if (createdAtUtc.Kind == DateTimeKind.Unspecified)
            createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        var job = new ImportJob(Guid.NewGuid(), coupleId, userId, storagePath, fileMimeType, createdAtUtc);
        job.SourceFileName = NormalizeFileName(sourceFileName);
        job.AiCategorizationConsent = aiCategorizationConsent;
        return job;
    }

    public void MarkProcessing(DateTime nowUtc)
    {
        Status = ImportJobStatus.Processing;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkReady(string ocrResultJson, DateTime nowUtc)
    {
        // A job that recovery already failed (or that is not being processed) never becomes Ready.
        if (Status != ImportJobStatus.Processing)
            throw new InvalidOperationException($"Uma importação em {Status} não pode ser marcada como pronta.");

        if (string.IsNullOrWhiteSpace(ocrResultJson))
            throw new ArgumentException("O resultado da leitura é obrigatório para concluir a importação.", nameof(ocrResultJson));

        OcrResultJson = ocrResultJson;
        Status = ImportJobStatus.Ready;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkFailed(string errorCode, string errorMessage, DateTime nowUtc, DateTime? quotaResetDate = null)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            throw new ArgumentException("O código do erro é obrigatório para marcar a importação como falha.", nameof(errorCode));

        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        QuotaResetDate = quotaResetDate;
        Status = ImportJobStatus.Failed;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    public void MarkConfirmed(DateTime nowUtc)
    {
        if (Status != ImportJobStatus.Ready)
            throw new InvalidOperationException($"Uma importação em {Status} não pode ser confirmada.");

        Status = ImportJobStatus.Confirmed;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    /// <summary>
    /// Resets the job to Pending for retry after a transient failure.
    /// Increments the retry counter.
    /// </summary>
    public void ResetForRetry(DateTime nowUtc)
    {
        RetryCount++;
        Status = ImportJobStatus.Pending;
        ErrorCode = null;
        ErrorMessage = null;
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    /// <summary>Outcome of one statement line; lines never touched are <see cref="ImportLineState.Pending"/>.</summary>
    public ImportLineState GetLineState(int index)
        => ReadLineStates().TryGetValue(index, out var state) ? state : ImportLineState.Pending;

    public void SetLineState(int index, ImportLineState state, DateTime nowUtc)
    {
        var states = ReadLineStates();
        if (state == ImportLineState.Pending)
            states.Remove(index);
        else
            states[index] = state;

        LineStatesJson = states.Count == 0
            ? null
            : JsonSerializer.Serialize(states.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString()));
        UpdatedAtUtc = NormalizeUtc(nowUtc);
    }

    /// <summary>True when the job has been in Processing since before <paramref name="nowUtc"/> minus <paramref name="timeout"/>.</summary>
    public bool IsStuckProcessing(DateTime nowUtc, TimeSpan timeout)
        => Status == ImportJobStatus.Processing && UpdatedAtUtc <= NormalizeUtc(nowUtc) - timeout;

    private Dictionary<int, ImportLineState> ReadLineStates()
    {
        var result = new Dictionary<int, ImportLineState>();
        if (string.IsNullOrWhiteSpace(LineStatesJson))
            return result;

        var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(LineStatesJson) ?? new();
        foreach (var (key, value) in raw)
        {
            if (int.TryParse(key, out var index) && Enum.TryParse<ImportLineState>(value, out var state))
                result[index] = state;
        }

        return result;
    }

    public bool CanRetry(int maxRetries) => RetryCount < maxRetries;

    private static string? NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = new string(Path.GetFileName(fileName.Replace('\\', '/')).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) return null;
        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] : name;
    }

    public const int MaxFileNameLength = 120;

    private static DateTime NormalizeUtc(DateTime dt) =>
        dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
}

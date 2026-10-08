using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public enum SyncRunStatus
{
    Pending,
    Running,
    Done,
    Failed
}

public enum SyncRunTrigger
{
    /// <summary>The person asked (the wizard, or "Sincronizar agora").</summary>
    User,

    /// <summary>The app asked in silence when it was opened.</summary>
    AppOpen,

    /// <summary>The daily scheduler of the server.</summary>
    Scheduler
}

/// <summary>
/// One synchronisation of a <see cref="BankConnection"/> with Pluggy: the queue of the job (<c>Pending</c>) and the
/// diary of what happened (<c>Done</c> / <c>Failed</c> with counts and the error).
/// </summary>
public sealed class SyncRun : ICoupleScoped
{
    public const int MaxErrorCodeLength = 64;
    public const int MaxErrorMessageLength = 512;

    private SyncRun() { }

    public Guid Id { get; private set; }
    public Guid CoupleId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public SyncRunStatus Status { get; private set; }
    public SyncRunTrigger TriggeredBy { get; private set; }

    /// <summary>Ask Pluggy to refresh each item at the bank before reading (only the manual button).</summary>
    public bool ForceItemUpdate { get; private set; }

    /// <summary>
    /// Who connected accepted the AI disclosure when asking for this run: only then may descriptions go to the AI
    /// classifier. Runs nobody asked for (the scheduler) never have it.
    /// </summary>
    public bool AiCategorizationConsent { get; private set; }

    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public int TransactionsNew { get; private set; }
    public int TransactionsUpdated { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public bool IsOpen => Status is SyncRunStatus.Pending or SyncRunStatus.Running;

    public static SyncRun Create(
        Guid coupleId,
        Guid connectionId,
        SyncRunTrigger triggeredBy,
        bool forceItemUpdate,
        bool aiCategorizationConsent,
        DateTime nowUtc)
        => new()
        {
            Id = Guid.NewGuid(),
            CoupleId = coupleId,
            ConnectionId = connectionId,
            Status = SyncRunStatus.Pending,
            TriggeredBy = triggeredBy,
            ForceItemUpdate = forceItemUpdate,
            AiCategorizationConsent = aiCategorizationConsent,
            CreatedAtUtc = nowUtc,
        };

    public void MarkRunning(DateTime nowUtc)
    {
        Status = SyncRunStatus.Running;
        StartedAtUtc = nowUtc;
    }

    public void AddCounts(int added, int updated)
    {
        TransactionsNew += added;
        TransactionsUpdated += updated;
    }

    public void MarkDone(DateTime nowUtc)
    {
        Status = SyncRunStatus.Done;
        FinishedAtUtc = nowUtc;
        ErrorCode = null;
        ErrorMessage = null;
    }

    /// <summary><paramref name="message"/> is shown to the person: Brazilian Portuguese.</summary>
    public void MarkFailed(string code, string message, DateTime nowUtc)
    {
        Status = SyncRunStatus.Failed;
        FinishedAtUtc = nowUtc;
        ErrorCode = code.Length > MaxErrorCodeLength ? code[..MaxErrorCodeLength] : code;
        ErrorMessage = message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message;
    }
}

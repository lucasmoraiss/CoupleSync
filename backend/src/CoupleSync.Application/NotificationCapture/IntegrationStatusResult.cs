namespace CoupleSync.Application.NotificationCapture;

public sealed record IntegrationStatusResult(
    bool IsActive,
    DateTime? LastEventAtUtc,
    DateTime? LastErrorAtUtc,
    string? LastErrorMessage,
    string? RecoveryHint,
    int TotalAccepted,
    int TotalDuplicate,
    int TotalRejected
);

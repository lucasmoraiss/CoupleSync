namespace CoupleSync.Api.Contracts.Integrations;

public sealed record IntegrationStatusResponse(
    bool IsActive,
    DateTime? LastEventAtUtc,
    DateTime? LastErrorAtUtc,
    string? LastErrorMessage,
    string? RecoveryHint,
    IntegrationCountsDto Counts
);

public sealed record IntegrationCountsDto(
    int TotalAccepted,
    int TotalDuplicate,
    int TotalRejected
);

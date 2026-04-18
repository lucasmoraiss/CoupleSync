namespace CoupleSync.Application.NotificationCapture;

public sealed record IngestNotificationEventCommand(
    Guid UserId,
    Guid CoupleId,
    string Bank,
    decimal Amount,
    string Currency,
    DateTime EventTimestamp,
    string? Description,
    string? Merchant,
    string? RawNotificationText  // will be sanitized in handler before persistence
);

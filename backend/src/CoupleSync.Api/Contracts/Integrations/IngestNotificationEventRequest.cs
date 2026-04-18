namespace CoupleSync.Api.Contracts.Integrations;

public sealed record IngestNotificationEventRequest(
    string Bank,
    decimal Amount,
    string Currency,
    DateTime EventTimestamp,
    string? Description,
    string? Merchant,
    string? RawNotificationText
);

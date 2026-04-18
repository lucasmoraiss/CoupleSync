namespace CoupleSync.Api.Contracts.Integrations;

public sealed record IngestNotificationEventResponse(Guid IngestId, string Status);

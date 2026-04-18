namespace CoupleSync.Application.Notification.Queries;

public sealed record GetNotificationSettingsQuery(Guid UserId, Guid CoupleId);

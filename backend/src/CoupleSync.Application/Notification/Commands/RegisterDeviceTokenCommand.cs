namespace CoupleSync.Application.Notification.Commands;

public sealed record RegisterDeviceTokenCommand(Guid UserId, Guid CoupleId, string Token);

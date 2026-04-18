namespace CoupleSync.Application.Notification.Commands;

public sealed record UpdateNotificationSettingsCommand(
    Guid UserId,
    Guid CoupleId,
    bool? LowBalanceEnabled,
    bool? LargeTransactionEnabled,
    bool? BillReminderEnabled);

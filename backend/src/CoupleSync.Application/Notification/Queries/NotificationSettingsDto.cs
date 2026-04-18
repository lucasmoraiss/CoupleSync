namespace CoupleSync.Application.Notification.Queries;

public sealed record NotificationSettingsDto(
    Guid UserId,
    bool LowBalanceEnabled,
    bool LargeTransactionEnabled,
    bool BillReminderEnabled,
    DateTime UpdatedAtUtc);

using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

public sealed class NotificationSettings : ICoupleScoped
{
    private NotificationSettings() { }

    private NotificationSettings(
        Guid id,
        Guid userId,
        Guid coupleId,
        DateTime nowUtc)
    {
        Id = id;
        UserId = userId;
        CoupleId = coupleId;
        LowBalanceEnabled = true;
        LargeTransactionEnabled = true;
        BillReminderEnabled = true;
        UpdatedAtUtc = nowUtc;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid CoupleId { get; private set; }
    public bool LowBalanceEnabled { get; private set; }
    public bool LargeTransactionEnabled { get; private set; }
    public bool BillReminderEnabled { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static NotificationSettings Create(Guid userId, Guid coupleId, DateTime nowUtc)
        => new(Guid.NewGuid(), userId, coupleId, nowUtc);

    public void Update(bool? lowBalance, bool? largeTransaction, bool? billReminder, DateTime nowUtc)
    {
        if (lowBalance.HasValue) LowBalanceEnabled = lowBalance.Value;
        if (largeTransaction.HasValue) LargeTransactionEnabled = largeTransaction.Value;
        if (billReminder.HasValue) BillReminderEnabled = billReminder.Value;
        UpdatedAtUtc = nowUtc;
    }
}

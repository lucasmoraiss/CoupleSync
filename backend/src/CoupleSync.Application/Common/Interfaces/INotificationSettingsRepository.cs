using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface INotificationSettingsRepository
{
    Task<NotificationSettings?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct);
    Task<NotificationSettings> UpsertAsync(Guid userId, Guid coupleId, bool? lowBalance, bool? largeTransaction, bool? billReminder, DateTime nowUtc, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

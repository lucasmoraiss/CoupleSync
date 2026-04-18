using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface INotificationEventRepository
{
    Task<IReadOnlyList<NotificationEvent>> GetPendingAsync(Guid coupleId, CancellationToken ct);
    Task<IReadOnlyList<NotificationEvent>> GetAllPendingAsync(CancellationToken ct);
    Task<bool> ExistsByAlertTypeAsync(Guid coupleId, string alertType, CancellationToken ct);
    Task AddRangeAsync(IReadOnlyList<NotificationEvent> events, CancellationToken ct);
    Task UpdateAsync(NotificationEvent @event, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeNotificationEventRepository : INotificationEventRepository
{
    public List<NotificationEvent> Events { get; } = new();

    public Task<IReadOnlyList<NotificationEvent>> GetPendingAsync(Guid coupleId, CancellationToken ct)
    {
        var result = Events.Where(e => e.CoupleId == coupleId && e.Status == "Pending").ToList();
        return Task.FromResult<IReadOnlyList<NotificationEvent>>(result);
    }

    public Task<IReadOnlyList<NotificationEvent>> GetAllPendingAsync(CancellationToken ct)
    {
        var result = Events.Where(e => e.Status == "Pending").ToList();
        return Task.FromResult<IReadOnlyList<NotificationEvent>>(result);
    }

    public Task<bool> ExistsByAlertTypeAsync(Guid coupleId, string alertType, CancellationToken ct)
    {
        var exists = Events.Any(e => e.CoupleId == coupleId && e.AlertType == alertType);
        return Task.FromResult(exists);
    }

    public Task AddRangeAsync(IReadOnlyList<NotificationEvent> events, CancellationToken ct)
    {
        Events.AddRange(events);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(NotificationEvent @event, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeNotificationSettingsRepository : INotificationSettingsRepository
{
    private readonly Dictionary<Guid, NotificationSettings> _store = new();

    public Task<NotificationSettings?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct)
    {
        _store.TryGetValue(userId, out var settings);
        return Task.FromResult(settings);
    }

    public Task<NotificationSettings> UpsertAsync(
        Guid userId,
        Guid coupleId,
        bool? lowBalance,
        bool? largeTransaction,
        bool? billReminder,
        DateTime nowUtc,
        CancellationToken ct)
    {
        if (!_store.TryGetValue(userId, out var settings))
        {
            settings = NotificationSettings.Create(userId, coupleId, nowUtc);
            _store[userId] = settings;
        }
        settings.Update(lowBalance, largeTransaction, billReminder, nowUtc);
        return Task.FromResult(settings);
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

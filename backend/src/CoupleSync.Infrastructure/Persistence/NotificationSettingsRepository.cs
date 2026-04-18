using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class NotificationSettingsRepository : INotificationSettingsRepository
{
    private readonly AppDbContext _dbContext;

    public NotificationSettingsRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<NotificationSettings?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.NotificationSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.CoupleId == coupleId, ct);
    }

    public async Task<NotificationSettings> UpsertAsync(
        Guid userId,
        Guid coupleId,
        bool? lowBalance,
        bool? largeTransaction,
        bool? billReminder,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var existing = await _dbContext.NotificationSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.CoupleId == coupleId, ct);

        if (existing is not null)
        {
            existing.Update(lowBalance, largeTransaction, billReminder, nowUtc);
            return existing;
        }

        var settings = NotificationSettings.Create(userId, coupleId, nowUtc);
        if (lowBalance.HasValue || largeTransaction.HasValue || billReminder.HasValue)
            settings.Update(lowBalance, largeTransaction, billReminder, nowUtc);

        await _dbContext.NotificationSettings.AddAsync(settings, ct);
        return settings;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}

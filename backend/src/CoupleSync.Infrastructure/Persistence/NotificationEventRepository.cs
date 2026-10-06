using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class NotificationEventRepository : INotificationEventRepository
{
    private readonly AppDbContext _dbContext;

    public NotificationEventRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<NotificationEvent>> GetPendingAsync(Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.NotificationEvents
            .Where(e => e.CoupleId == coupleId && e.Status == "Pending")
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationEvent>> GetAllPendingAsync(CancellationToken ct)
    {
        return await _dbContext.NotificationEvents
            .Where(e => e.Status == "Pending")
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> GetAlertTypesSinceAsync(Guid coupleId, DateTime sinceUtc, CancellationToken ct)
    {
        return await _dbContext.NotificationEvents
            .Where(e => e.CoupleId == coupleId && e.CreatedAtUtc >= sinceUtc)
            .Select(e => e.AlertType)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task AddRangeAsync(IReadOnlyList<NotificationEvent> events, CancellationToken ct)
    {
        await _dbContext.NotificationEvents.AddRangeAsync(events, ct);
    }

    public Task UpdateAsync(NotificationEvent @event, CancellationToken ct)
    {
        _dbContext.NotificationEvents.Update(@event);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return DbSaveTranslator.SaveAsync(_dbContext, ct);
    }
}

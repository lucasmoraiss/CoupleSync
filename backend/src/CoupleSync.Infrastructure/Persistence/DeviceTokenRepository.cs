using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class DeviceTokenRepository : IDeviceTokenRepository
{
    private readonly AppDbContext _dbContext;

    public DeviceTokenRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DeviceToken?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct)
    {
        return await _dbContext.DeviceTokens
            .FirstOrDefaultAsync(d => d.UserId == userId && d.CoupleId == coupleId, ct);
    }

    public async Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(Guid userId, CancellationToken ct)
    {
        return await _dbContext.DeviceTokens
            .Where(d => d.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(Guid userId, Guid coupleId, string token, DateTime nowUtc, CancellationToken ct)
    {
        const string platform = "android";

        // A token belongs to a single user: a device that switched accounts hands its token over, so it
        // stops receiving the previous account's alerts. The previous owner may be in another couple,
        // hence IgnoreQueryFilters.
        var heldByOthers = await _dbContext.DeviceTokens
            .IgnoreQueryFilters()
            .Where(d => d.Token == token && d.UserId != userId)
            .ToListAsync(ct);
        _dbContext.DeviceTokens.RemoveRange(heldByOthers);

        var existing = await _dbContext.DeviceTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.Platform == platform, ct);

        if (existing is not null)
        {
            existing.Refresh(coupleId, token, nowUtc);
        }
        else
        {
            var deviceToken = DeviceToken.Create(userId, coupleId, token, nowUtc);
            await _dbContext.DeviceTokens.AddAsync(deviceToken, ct);
        }
    }

    public async Task DeleteForUserAsync(Guid userId, string token, CancellationToken ct)
    {
        // The registration may sit under a group the caller no longer belongs to, hence IgnoreQueryFilters.
        await _dbContext.DeviceTokens
            .IgnoreQueryFilters()
            .Where(d => d.UserId == userId && d.Token == token)
            .ExecuteDeleteAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }

    public void DiscardPendingChanges()
    {
        _dbContext.ChangeTracker.Clear();
    }
}

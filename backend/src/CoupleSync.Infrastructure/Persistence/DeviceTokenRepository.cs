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

        var existing = await _dbContext.DeviceTokens
            .FirstOrDefaultAsync(d => d.UserId == userId && d.CoupleId == coupleId && d.Platform == platform, ct);

        if (existing is not null)
        {
            existing.UpdateLastSeen(token, nowUtc);
        }
        else
        {
            var deviceToken = DeviceToken.Create(userId, coupleId, token, nowUtc);
            await _dbContext.DeviceTokens.AddAsync(deviceToken, ct);
        }
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}

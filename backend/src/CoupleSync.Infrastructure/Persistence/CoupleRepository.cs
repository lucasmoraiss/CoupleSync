using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CoupleSync.Infrastructure.Persistence;

public sealed class CoupleRepository : ICoupleRepository
{
    private readonly AppDbContext _dbContext;

    public CoupleRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<User?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return _dbContext.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
    }

    public Task<Couple?> FindByJoinCodeAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();

        return _dbContext.Couples
            .Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.JoinCode == normalizedJoinCode, cancellationToken);
    }

    public Task<Couple?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Couples
            .Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.Id == coupleId, cancellationToken);
    }

    public Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Users
            .AsNoTracking()
            .Where(user => user.CoupleId == coupleId)
            .ToDictionaryAsync(user => user.Id, user => user.Name, cancellationToken);
    }

    public Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();
        return _dbContext.Couples.AnyAsync(x => x.JoinCode == normalizedJoinCode, cancellationToken);
    }

    public async Task StopDeliveriesToMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken)
    {
        // The query filters follow the caller's couple claim, which may not be this couple's (a leaver whose
        // token is stale): bypass them and state the couple explicitly.
        var deviceTokens = await _dbContext.DeviceTokens
            .IgnoreQueryFilters()
            .Where(d => d.UserId == userId && d.CoupleId == coupleId)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceTokens.RemoveRange(deviceTokens);

        var pendingAlerts = await _dbContext.NotificationEvents
            .IgnoreQueryFilters()
            .Where(e => e.UserId == userId && e.CoupleId == coupleId && e.Status == "Pending")
            .ToListAsync(cancellationToken);
        foreach (var alert in pendingAlerts)
        {
            alert.MarkFailed();
        }
    }

    public async Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken)
    {
        var refreshTokens = await _dbContext.RefreshTokens
            .Where(r => r.UserId == userId)
            .ToListAsync(cancellationToken);
        _dbContext.RefreshTokens.RemoveRange(refreshTokens);
    }

    public async Task AddCoupleAsync(Couple couple, CancellationToken cancellationToken)
    {
        await _dbContext.Couples.AddAsync(couple, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return DbSaveTranslator.SaveAsync(_dbContext, cancellationToken);
    }
}
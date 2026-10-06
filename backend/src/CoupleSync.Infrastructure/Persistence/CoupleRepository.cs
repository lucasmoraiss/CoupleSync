using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Application.Couples;
using CoupleSync.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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

    public async Task<Guid?> FindIdByJoinCodeAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();

        var ids = await _dbContext.Couples
            .AsNoTracking()
            .Where(x => x.JoinCode == normalizedJoinCode)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return ids.Count == 0 ? null : ids[0];
    }

    public Task<Couple?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.Couples
            .Include(x => x.Members)
            .ThenInclude(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == coupleId, cancellationToken);
    }

    public Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken cancellationToken)
    {
        return _dbContext.CoupleMembers
            .AsNoTracking()
            .Where(member => member.CoupleId == coupleId)
            .Select(member => new { member.UserId, member.User.Name })
            .ToDictionaryAsync(member => member.UserId, member => member.Name, cancellationToken);
    }

    public async Task<IReadOnlyList<UserGroup>> GetGroupsOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Starts from the user's own membership rows: a group the user is not in cannot appear.
        var memberships = await _dbContext.CoupleMembers
            .AsNoTracking()
            .Where(member => member.UserId == userId)
            .Select(member => new { member.CoupleId, member.Role, member.JoinedAtUtc })
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
        {
            return [];
        }

        var coupleIds = memberships.Select(m => m.CoupleId).ToList();
        var members = await _dbContext.CoupleMembers
            .AsNoTracking()
            .Where(member => coupleIds.Contains(member.CoupleId))
            .Select(member => new { member.CoupleId, member.UserId, member.User.Name, member.JoinedAtUtc })
            .ToListAsync(cancellationToken);

        return memberships
            .OrderBy(m => m.JoinedAtUtc)
            .ThenBy(m => m.CoupleId)
            .Select(m => new UserGroup(
                m.CoupleId,
                m.Role,
                m.JoinedAtUtc,
                members
                    .Where(x => x.CoupleId == m.CoupleId)
                    .OrderBy(x => x.JoinedAtUtc)
                    .ThenBy(x => x.UserId)
                    .Select(x => new GroupMemberName(x.UserId, x.Name))
                    .ToList()))
            .ToList();
    }

    public Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken)
    {
        var normalizedJoinCode = joinCode.Trim().ToUpperInvariant();
        return _dbContext.Couples.AnyAsync(x => x.JoinCode == normalizedJoinCode, cancellationToken);
    }

    public async Task StopDeliveriesToMemberAsync(Guid userId, Guid coupleId, Guid? remainingCoupleId, CancellationToken cancellationToken)
    {
        // The query filters follow the caller's couple claim, which may not be this couple's (a leaver whose
        // token is stale, an owner removing someone): bypass them and state user and couple explicitly.
        var deviceTokens = await _dbContext.DeviceTokens
            .IgnoreQueryFilters()
            .Where(d => d.UserId == userId)
            .ToListAsync(cancellationToken);

        if (remainingCoupleId is null)
        {
            _dbContext.DeviceTokens.RemoveRange(deviceTokens);
        }
        else
        {
            foreach (var deviceToken in deviceTokens.Where(d => d.CoupleId == coupleId))
            {
                deviceToken.MoveToCouple(remainingCoupleId.Value);
            }
        }

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

    public async Task<IMembershipChange> BeginMembershipChangeAsync(Guid userId, CancellationToken cancellationToken)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var change = new MembershipChange(_dbContext, transaction);
        try
        {
            await change.LockRowAsync("users", userId, cancellationToken);
        }
        catch
        {
            await change.DisposeAsync();
            throw;
        }

        return change;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return DbSaveTranslator.SaveAsync(_dbContext, cancellationToken);
    }

    /// <summary>
    /// The transaction of one membership change. On PostgreSQL the rows are locked with FOR NO KEY UPDATE: it
    /// queues other membership changes of the same user or group, and (unlike FOR UPDATE) does not hold up
    /// ordinary inserts that merely reference the user or the group. Other providers (the SQLite used by tests)
    /// have a single writer and take no lock.
    /// </summary>
    private sealed class MembershipChange : IMembershipChange
    {
        private readonly AppDbContext _dbContext;
        private readonly IDbContextTransaction _transaction;

        public MembershipChange(AppDbContext dbContext, IDbContextTransaction transaction)
        {
            _dbContext = dbContext;
            _transaction = transaction;
        }

        public Task LockCoupleAsync(Guid coupleId, CancellationToken cancellationToken) =>
            LockRowAsync("couples", coupleId, cancellationToken);

        public async Task LockRowAsync(string table, Guid id, CancellationToken cancellationToken)
        {
            if (_dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            // The table name is one of two constants above, never input; the id is a parameter.
            var sql = table == "users"
                ? "SELECT 1 FROM users WHERE id = {0} FOR NO KEY UPDATE"
                : "SELECT 1 FROM couples WHERE id = {0} FOR NO KEY UPDATE";
            await _dbContext.Database.ExecuteSqlRawAsync(sql, [id], cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync(); // rolls back when not committed
    }
}

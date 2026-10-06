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

    public async Task<IDeviceTokenRegistration> BeginRegistrationAsync(Guid userId, string token, CancellationToken ct)
    {
        var isPostgres = _dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        if (!isPostgres)
        {
            return new NoopRegistration();
        }

        var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        try
        {
            // Always taken in the same (numeric) order, so two registrations can never wait for each other.
            foreach (var key in new[] { AdvisoryKey("token:" + token), AdvisoryKey("user:" + userId.ToString("N")) }.Order())
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", ct);
            }
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        return new TransactionRegistration(transaction);
    }

    private static long AdvisoryKey(string text) =>
        BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0);

    private sealed class NoopRegistration : IDeviceTokenRegistration
    {
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransactionRegistration : IDeviceTokenRegistration
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;

        public TransactionRegistration(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction) =>
            _transaction = transaction;

        public Task CommitAsync(CancellationToken ct) => _transaction.CommitAsync(ct);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync(); // rolls back when not committed
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

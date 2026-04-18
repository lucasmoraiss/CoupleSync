using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeDeviceTokenRepository : IDeviceTokenRepository
{
    private readonly List<DeviceToken> _tokens = new();

    public void Add(DeviceToken token) => _tokens.Add(token);

    public Task<DeviceToken?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct)
    {
        var result = _tokens.FirstOrDefault(d => d.UserId == userId && d.CoupleId == coupleId);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(Guid userId, CancellationToken ct)
    {
        var result = _tokens.Where(d => d.UserId == userId).ToList();
        return Task.FromResult<IReadOnlyList<DeviceToken>>(result);
    }

    public Task UpsertAsync(Guid userId, Guid coupleId, string token, DateTime nowUtc, CancellationToken ct)
    {
        var existing = _tokens.FirstOrDefault(d => d.UserId == userId && d.CoupleId == coupleId);
        if (existing is not null)
            existing.UpdateLastSeen(token, nowUtc);
        else
            _tokens.Add(DeviceToken.Create(userId, coupleId, token, nowUtc));
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
}

using CoupleSync.Application.Common.Interfaces;
using CoupleSync.Domain.Entities;

namespace CoupleSync.UnitTests.Support;

public sealed class FakeDeviceTokenRepository : IDeviceTokenRepository
{
    private readonly List<DeviceToken> _tokens = new();

    public void Add(DeviceToken token) => _tokens.Add(token);

    public IReadOnlyList<DeviceToken> All => _tokens;

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
        _tokens.RemoveAll(d => d.Token == token && d.UserId != userId);
        var existing = _tokens.FirstOrDefault(d => d.UserId == userId);
        if (existing is not null)
            existing.Refresh(coupleId, token, nowUtc);
        else
            _tokens.Add(DeviceToken.Create(userId, coupleId, token, nowUtc));
        return Task.CompletedTask;
    }

    public Task DeleteForUserAsync(Guid userId, string token, CancellationToken ct)
    {
        _tokens.RemoveAll(d => d.UserId == userId && d.Token == token);
        return Task.CompletedTask;
    }

    /// <summary>Exceptions to throw from the next SaveChangesAsync calls, one per call (simulates losing an insert race).</summary>
    public Queue<Exception> SaveFailures { get; } = new();

    public int SaveCalls { get; private set; }

    public int DiscardCalls { get; private set; }

    public int BeginCalls { get; private set; }

    public int CommitCalls { get; private set; }

    public Task<IDeviceTokenRegistration> BeginRegistrationAsync(Guid userId, string token, CancellationToken ct)
    {
        BeginCalls++;
        return Task.FromResult<IDeviceTokenRegistration>(new Registration(this));
    }

    private sealed class Registration : IDeviceTokenRegistration
    {
        private readonly FakeDeviceTokenRepository _owner;

        public Registration(FakeDeviceTokenRepository owner) => _owner = owner;

        public Task CommitAsync(CancellationToken ct)
        {
            _owner.CommitCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        SaveCalls++;
        if (SaveFailures.Count > 0) throw SaveFailures.Dequeue();
        return Task.CompletedTask;
    }

    public void DiscardPendingChanges() => DiscardCalls++;
}

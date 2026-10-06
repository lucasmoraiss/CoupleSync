using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IDeviceTokenRepository
{
    Task<DeviceToken?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct);
    Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task UpsertAsync(Guid userId, Guid coupleId, string token, DateTime nowUtc, CancellationToken ct);
    /// <summary>Deletes the registration of this push token when it belongs to this user; no-op otherwise. Applies immediately.</summary>
    Task DeleteForUserAsync(Guid userId, string token, CancellationToken ct);
    /// <summary>
    /// Starts one registration (token + user). On PostgreSQL it opens a transaction holding advisory locks for the token
    /// and for the user, so concurrent registrations queue up instead of racing on the unique indexes; on other providers
    /// it does nothing and the caller's bounded retry is the only protection. Call CommitAsync, then dispose.
    /// </summary>
    Task<IDeviceTokenRegistration> BeginRegistrationAsync(Guid userId, string token, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    /// <summary>Forgets changes that were staged but not saved (after a failed save), so the operation can be redone from a clean state.</summary>
    void DiscardPendingChanges();
}

public interface IDeviceTokenRegistration : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

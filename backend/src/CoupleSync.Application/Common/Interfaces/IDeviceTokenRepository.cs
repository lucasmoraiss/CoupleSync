using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IDeviceTokenRepository
{
    Task<DeviceToken?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct);
    Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task UpsertAsync(Guid userId, Guid coupleId, string token, DateTime nowUtc, CancellationToken ct);
    /// <summary>Deletes the registration of this push token when it belongs to this user; no-op otherwise. Applies immediately.</summary>
    Task DeleteForUserAsync(Guid userId, string token, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    /// <summary>Forgets changes that were staged but not saved (after a failed save), so the operation can be redone from a clean state.</summary>
    void DiscardPendingChanges();
}

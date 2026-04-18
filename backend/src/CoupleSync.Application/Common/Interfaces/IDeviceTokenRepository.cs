using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

public interface IDeviceTokenRepository
{
    Task<DeviceToken?> GetByUserIdAsync(Guid userId, Guid coupleId, CancellationToken ct);
    Task<IReadOnlyList<DeviceToken>> GetByUserIdAsync(Guid userId, CancellationToken ct);
    Task UpsertAsync(Guid userId, Guid coupleId, string token, DateTime nowUtc, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

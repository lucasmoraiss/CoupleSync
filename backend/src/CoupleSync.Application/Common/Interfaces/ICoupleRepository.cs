using CoupleEntityType = CoupleSync.Domain.Entities.Couple;
using UserEntityType = CoupleSync.Domain.Entities.User;

namespace CoupleSync.Application.Common.Interfaces;

public interface ICoupleRepository
{
    Task<UserEntityType?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    Task<CoupleEntityType?> FindByJoinCodeAsync(string joinCode, CancellationToken cancellationToken);

    Task<CoupleEntityType?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken);

    /// <summary>Display names of the couple's members, by user id.</summary>
    Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken cancellationToken);

    Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken);

    /// <summary>
    /// Stops everything addressed to the user on behalf of the group: deletes the user's device tokens for it
    /// and fails the alerts still pending for them. Applied on the next <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task StopDeliveriesToMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken);

    /// <summary>Deletes the user's refresh token so the session cannot be renewed. Applied on the next <see cref="SaveChangesAsync"/>.</summary>
    Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken);

    Task AddCoupleAsync(CoupleEntityType couple, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
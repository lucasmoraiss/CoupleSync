using CoupleSync.Application.Couples;
using CoupleEntityType = CoupleSync.Domain.Entities.Couple;
using UserEntityType = CoupleSync.Domain.Entities.User;

namespace CoupleSync.Application.Common.Interfaces;

public interface ICoupleRepository
{
    Task<UserEntityType?> FindUserByIdAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The id of the group that currently answers to this invite code (not tracked, no lock).</summary>
    Task<Guid?> FindIdByJoinCodeAsync(string joinCode, CancellationToken cancellationToken);

    Task<CoupleEntityType?> FindByIdWithMembersAsync(Guid coupleId, CancellationToken cancellationToken);

    /// <summary>Display names of the couple's current members, by user id.</summary>
    Task<Dictionary<Guid, string>> GetMemberNamesAsync(Guid coupleId, CancellationToken cancellationToken);

    /// <summary>Every group the user belongs to (and only those), oldest membership first, with its members' names.</summary>
    Task<IReadOnlyList<UserGroup>> GetGroupsOfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> JoinCodeExistsAsync(string joinCode, CancellationToken cancellationToken);

    /// <summary>
    /// Stops what the group was sending to the user: fails the alerts still pending for them in that group.
    /// The user's devices follow the user: registrations made under the group move to
    /// <paramref name="remainingCoupleId"/>; with none left (null) all of the user's device registrations are
    /// deleted. Applied on the next <see cref="SaveChangesAsync"/>.
    /// </summary>
    Task StopDeliveriesToMemberAsync(Guid userId, Guid coupleId, Guid? remainingCoupleId, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the user's Open Finance out of the group: their bank connection there (with the stored credentials),
    /// its items and their accounts are deleted. Their connections in other groups and the connections of the
    /// other members are not touched. Nothing is decrypted, so it works on a server without the encryption key.
    /// Applied on the next <see cref="SaveChangesAsync"/>. Returns the ids of the connections being deleted.
    /// </summary>
    Task<IReadOnlyList<Guid>> RemoveOpenFinanceOfMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken);

    /// <summary>Deletes the user's refresh token so the session cannot be renewed. Applied on the next <see cref="SaveChangesAsync"/>.</summary>
    Task RevokeRefreshTokenAsync(Guid userId, CancellationToken cancellationToken);

    Task AddCoupleAsync(CoupleEntityType couple, CancellationToken cancellationToken);

    /// <summary>
    /// Starts one change of group membership for the user (create, join, leave, removal, switch, code renewal):
    /// opens a transaction and, on PostgreSQL, locks the user's row, so the changes of one user's memberships
    /// run one at a time. Read the state to decide on only AFTER this (and after
    /// <see cref="IMembershipChange.LockCoupleAsync"/> when a group is involved). Commit, then dispose.
    /// </summary>
    Task<IMembershipChange> BeginMembershipChangeAsync(Guid userId, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IMembershipChange : IAsyncDisposable
{
    /// <summary>
    /// Locks the group's row (PostgreSQL), always after the user's: changes to one group's members, owner and
    /// invite code run one at a time, so none of them decides on a state another is about to change.
    /// </summary>
    Task LockCoupleAsync(Guid coupleId, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

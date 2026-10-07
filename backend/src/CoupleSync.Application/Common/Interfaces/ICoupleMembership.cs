namespace CoupleSync.Application.Common.Interfaces;

/// <summary>
/// The one place that answers "does this user belong to this group" and "does this user own it" from the
/// database, never from a token claim: an access token outlives a removal by up to its lifetime.
/// Everything that grants access to a group's data or to a management action goes through here, so
/// a later change in how membership is stored (several groups per user, roles) only touches the implementation.
/// </summary>
public interface ICoupleMembership
{
    /// <summary>True when the user is active and currently a member of the group (one indexed lookup).</summary>
    Task<bool> IsMemberAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken);

    /// <summary>True when the user is a current member of the group and its registered owner.</summary>
    Task<bool> IsOwnerAsync(Guid userId, Guid coupleId, CancellationToken cancellationToken);
}

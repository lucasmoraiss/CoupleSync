using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>An acceptance in force, as the group sees it: "accepted by {Name} on {AcceptedAtUtc}".</summary>
public sealed record AiAcceptance(Guid UserId, string Name, DateTime AcceptedAtUtc);

/// <summary>
/// ai_consents, ai_user_preferences and the group's own "AI off" (couples.ai_disabled_at_utc). Every method names
/// the group: the gateway also asks from background work, where there is no group of a token for the global filter
/// to use.
/// </summary>
public interface IAiActivationRepository
{
    /// <summary>
    /// The acceptances the status lists: of this version, not revoked, of who is still an active member (checked in
    /// couple_members), oldest first. They do not decide whether the group has the AI.
    /// </summary>
    Task<IReadOnlyList<AiAcceptance>> GetAcceptancesAsync(Guid coupleId, int version, CancellationToken ct);

    /// <summary>
    /// The group exists and no member switched the AI off for it. No acceptance is needed: a group that never
    /// answered anything is on. A group that does not exist is not.
    /// </summary>
    Task<bool> IsEnabledAsync(Guid coupleId, CancellationToken ct);

    /// <summary>The group itself, to switch its AI off or back on. Null when it does not exist.</summary>
    Task<Couple?> FindGroupAsync(Guid coupleId, CancellationToken ct);

    /// <summary>The row of (group, person, version), revoked or not, to be changed.</summary>
    Task<AiConsent?> FindConsentAsync(Guid coupleId, Guid userId, int version, CancellationToken ct);

    /// <summary>The acceptances of the group that are not revoked, of any version, to be changed.</summary>
    Task<IReadOnlyList<AiConsent>> GetActiveConsentsAsync(Guid coupleId, CancellationToken ct);

    Task AddConsentAsync(AiConsent consent, CancellationToken ct);

    Task<AiUserPreference?> FindPreferenceAsync(Guid coupleId, Guid userId, CancellationToken ct);

    Task AddPreferenceAsync(AiUserPreference preference, CancellationToken ct);

    Task<bool> IsEmailVerifiedAsync(Guid userId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}

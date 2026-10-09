using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Common.Interfaces;

/// <summary>An acceptance in force, as the group sees it: "switched on by {Name} on {AcceptedAtUtc}".</summary>
public sealed record AiAcceptance(Guid UserId, string Name, DateTime AcceptedAtUtc);

/// <summary>
/// ai_consents and ai_user_preferences. Every method names the group: the gateway also asks from background work,
/// where there is no group of a token for the global filter to use.
/// </summary>
public interface IAiActivationRepository
{
    /// <summary>
    /// The acceptances that switch the group on: of this version, not revoked, of who is still an active member
    /// (checked in couple_members), oldest first.
    /// </summary>
    Task<IReadOnlyList<AiAcceptance>> GetAcceptancesAsync(Guid coupleId, int version, CancellationToken ct);

    /// <summary>True when <see cref="GetAcceptancesAsync"/> would return at least one row.</summary>
    Task<bool> IsEnabledAsync(Guid coupleId, int version, CancellationToken ct);

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

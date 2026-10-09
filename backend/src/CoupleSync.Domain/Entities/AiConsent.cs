using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

/// <summary>
/// One person's acceptance of the AI analysis for one group, for one version of the text: one row per
/// (group, person, version). The group has the AI switched on while at least one acceptance of
/// <see cref="CurrentVersion"/>, not revoked, belongs to someone who is still a member. Accepting again after a
/// revocation updates this same row.
/// </summary>
public sealed class AiConsent : ICoupleScoped
{
    /// <summary>
    /// The version of the AI text in force. A relevant change of the text raises it: every group goes back to "off".
    /// 1: only Google (Gemini). 2: Google (Gemini) and Groq, the second provider.
    /// </summary>
    public const int CurrentVersion = 2;

    private AiConsent()
    {
    }

    public Guid Id { get; private set; }

    public Guid CoupleId { get; private set; }

    public Guid UserId { get; private set; }

    public int Version { get; private set; }

    public DateTime AcceptedAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    /// <summary>Who switched it off: the person themselves, another member ("off for the group"), or the person who left.</summary>
    public Guid? RevokedByUserId { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static AiConsent Accept(Guid coupleId, Guid userId, int version, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        CoupleId = coupleId,
        UserId = userId,
        Version = version,
        AcceptedAtUtc = AsUtc(nowUtc),
    };

    /// <summary>Accepting again: the same row, with a new date and no revocation. An acceptance in force keeps its date.</summary>
    public void AcceptAgain(DateTime nowUtc)
    {
        if (IsActive) return;

        AcceptedAtUtc = AsUtc(nowUtc);
        RevokedAtUtc = null;
        RevokedByUserId = null;
    }

    public void Revoke(DateTime nowUtc, Guid revokedByUserId)
    {
        if (!IsActive) return;

        RevokedAtUtc = AsUtc(nowUtc);
        RevokedByUserId = revokedByUserId;
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

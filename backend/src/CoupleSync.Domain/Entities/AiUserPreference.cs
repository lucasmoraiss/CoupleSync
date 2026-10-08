using CoupleSync.Domain.Interfaces;

namespace CoupleSync.Domain.Entities;

/// <summary>
/// What one person chose about the AI inside one group: whether the welcome question was answered (so it does not
/// come back on another device) and whether they want the weekly e-mail. One row per (group, person); deleted when
/// the person leaves the group.
/// </summary>
public sealed class AiUserPreference : ICoupleScoped
{
    private AiUserPreference()
    {
    }

    public Guid Id { get; private set; }

    public Guid CoupleId { get; private set; }

    public Guid UserId { get; private set; }

    public bool WeeklyEmailEnabled { get; private set; }

    public DateTime? OnboardingAnsweredAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    public static AiUserPreference Create(Guid coupleId, Guid userId, DateTime nowUtc) => new()
    {
        Id = Guid.NewGuid(),
        CoupleId = coupleId,
        UserId = userId,
        UpdatedAtUtc = AsUtc(nowUtc),
    };

    /// <summary>The person saw the welcome screen (or the "X switched it on" notice) and answered it now.</summary>
    public void AnswerOnboarding(DateTime nowUtc)
    {
        OnboardingAnsweredAtUtc = AsUtc(nowUtc);
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    public void SetWeeklyEmail(bool enabled, DateTime nowUtc)
    {
        WeeklyEmailEnabled = enabled;
        UpdatedAtUtc = AsUtc(nowUtc);
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

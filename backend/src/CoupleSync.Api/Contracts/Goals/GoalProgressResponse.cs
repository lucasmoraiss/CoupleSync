namespace CoupleSync.Api.Contracts.Goals;

public sealed record GoalProgressResponse(
    Guid GoalId,
    string Title,
    decimal TargetAmount,
    decimal ContributedAmount,
    decimal ProgressPercent,
    bool IsAchieved,
    double DaysRemaining,
    string Status);

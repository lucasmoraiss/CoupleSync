using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals.Queries;

public sealed record GoalProgressResult(
    Guid GoalId,
    string Title,
    decimal TargetAmount,
    decimal ContributedAmount,
    decimal ProgressPercent,
    bool IsAchieved,
    double DaysRemaining,
    GoalStatus Status);

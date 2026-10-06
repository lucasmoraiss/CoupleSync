using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals.Queries;

/// <summary>ContributedAmount is the unified progress (manual + linked); the breakdown follows.</summary>
public sealed record GoalProgressResult(
    Guid GoalId,
    string Title,
    decimal TargetAmount,
    decimal ContributedAmount,
    decimal ProgressPercent,
    bool IsAchieved,
    double DaysRemaining,
    GoalStatus Status,
    decimal ManualAmount,
    decimal LinkedAmount);

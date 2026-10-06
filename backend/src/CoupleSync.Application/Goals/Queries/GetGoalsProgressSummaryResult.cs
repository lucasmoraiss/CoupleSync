namespace CoupleSync.Application.Goals.Queries;

/// <summary>CurrentAmount is the unified progress (manual + linked); the breakdown follows.</summary>
public sealed record GoalProgressSummaryItem(
    Guid Id,
    string Title,
    decimal TargetAmount,
    decimal CurrentAmount,
    decimal ProgressPercent,
    bool IsAchieved,
    DateTime Deadline,
    decimal ManualAmount,
    decimal LinkedAmount);

public sealed record GetGoalsProgressSummaryResult(IReadOnlyList<GoalProgressSummaryItem> Goals);

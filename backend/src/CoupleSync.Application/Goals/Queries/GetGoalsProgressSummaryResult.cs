namespace CoupleSync.Application.Goals.Queries;

public sealed record GoalProgressSummaryItem(
    Guid Id,
    string Title,
    decimal TargetAmount,
    decimal CurrentAmount,
    decimal ProgressPercent,
    bool IsAchieved,
    DateTime Deadline);

public sealed record GetGoalsProgressSummaryResult(IReadOnlyList<GoalProgressSummaryItem> Goals);

using CoupleSync.Application.Goals;
using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals.Queries;

/// <summary>
/// CurrentAmount is the unified progress (manual + linked transactions); the breakdown is in
/// ManualAmount / LinkedAmount.
/// </summary>
public sealed record GoalDto(
    Guid Id,
    Guid CreatedByUserId,
    string Title,
    string? Description,
    decimal TargetAmount,
    decimal CurrentAmount,
    string Currency,
    DateTime Deadline,
    GoalStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    decimal ManualAmount,
    decimal LinkedAmount,
    decimal ProgressPercent,
    bool IsAchieved)
{
    public static GoalDto From(Goal goal, GoalProgressBreakdown progress) => new(
        goal.Id,
        goal.CreatedByUserId,
        goal.Title,
        goal.Description,
        goal.TargetAmount,
        progress.TotalAmount,
        goal.Currency,
        goal.Deadline,
        goal.Status,
        goal.CreatedAtUtc,
        goal.UpdatedAtUtc,
        progress.ManualAmount,
        progress.LinkedAmount,
        progress.ProgressPercent,
        progress.IsAchieved);
}

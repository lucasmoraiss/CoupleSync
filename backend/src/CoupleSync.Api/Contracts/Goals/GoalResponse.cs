using CoupleSync.Domain.Entities;

namespace CoupleSync.Api.Contracts.Goals;

/// <summary>
/// CurrentAmount is the unified progress (manually saved + linked transactions), which is what the app
/// shows; ManualAmount / LinkedAmount are its two parts.
/// </summary>
public sealed record GoalResponse(
    Guid Id,
    Guid CreatedByUserId,
    string Title,
    string? Description,
    decimal TargetAmount,
    decimal CurrentAmount,
    string Currency,
    DateTime Deadline,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    decimal ManualAmount,
    decimal LinkedAmount,
    decimal ProgressPercent,
    bool IsAchieved);

public sealed record GetGoalsResponse(int TotalCount, IReadOnlyList<GoalResponse> Items);

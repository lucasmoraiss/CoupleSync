using CoupleSync.Domain.Entities;

namespace CoupleSync.Api.Contracts.Goals;

public sealed record GoalResponse(
    Guid Id,
    Guid CreatedByUserId,
    string Title,
    string? Description,
    decimal TargetAmount,
    string Currency,
    DateTime Deadline,
    string Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record GetGoalsResponse(int TotalCount, IReadOnlyList<GoalResponse> Items);

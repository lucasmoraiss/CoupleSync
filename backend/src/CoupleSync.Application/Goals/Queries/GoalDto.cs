using CoupleSync.Domain.Entities;

namespace CoupleSync.Application.Goals.Queries;

public sealed record GoalDto(
    Guid Id,
    Guid CreatedByUserId,
    string Title,
    string? Description,
    decimal TargetAmount,
    string Currency,
    DateTime Deadline,
    GoalStatus Status,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

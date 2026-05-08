namespace CoupleSync.Application.Goals.Commands;

public sealed record UpdateGoalCommand(
    Guid Id,
    Guid CoupleId,
    string? Title,
    string? Description,
    decimal? TargetAmount,
    decimal? CurrentAmount,
    DateTime? Deadline);

namespace CoupleSync.Application.Goals.Commands;

public sealed record CreateGoalCommand(
    Guid CoupleId,
    Guid CreatedByUserId,
    string Title,
    string? Description,
    decimal TargetAmount,
    string Currency,
    DateTime Deadline);

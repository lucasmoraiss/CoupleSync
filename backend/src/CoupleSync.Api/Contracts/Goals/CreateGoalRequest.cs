namespace CoupleSync.Api.Contracts.Goals;

public sealed record CreateGoalRequest(
    string Title,
    string? Description,
    decimal TargetAmount,
    string? Currency,
    DateTime Deadline);

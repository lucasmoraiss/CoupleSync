namespace CoupleSync.Api.Contracts.Goals;

public sealed record UpdateGoalRequest(
    string? Title,
    string? Description,
    decimal? TargetAmount,
    DateTime? Deadline);

namespace CoupleSync.Api.Contracts.Goals;

public sealed record UpdateGoalRequest(
    string? Title,
    string? Description,
    decimal? TargetAmount,
    decimal? CurrentAmount,
    DateTime? Deadline);

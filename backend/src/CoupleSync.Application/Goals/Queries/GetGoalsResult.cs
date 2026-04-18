namespace CoupleSync.Application.Goals.Queries;

public sealed record GetGoalsResult(int TotalCount, IReadOnlyList<GoalDto> Items);

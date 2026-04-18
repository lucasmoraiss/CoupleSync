namespace CoupleSync.Application.Goals.Queries;

public sealed record GetGoalProgressQuery(Guid GoalId, Guid CoupleId);

namespace CoupleSync.Application.Goals.Queries;

public sealed record GetGoalByIdQuery(Guid Id, Guid CoupleId);

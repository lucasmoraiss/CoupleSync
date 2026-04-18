namespace CoupleSync.Application.Goals.Queries;

public sealed record GetGoalsQuery(Guid CoupleId, bool IncludeArchived);

namespace CoupleSync.Application.Goals.Commands;

public sealed record DeleteGoalCommand(Guid Id, Guid CoupleId);
